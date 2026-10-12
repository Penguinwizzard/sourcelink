// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Microsoft.Build.Tasks.Git;

/// <summary>
/// Reads the chunk table shared by Git commit-graph and multi-pack-index files.
/// See https://git-scm.com/docs/gitformat-chunk
/// </summary>
internal sealed class GitChunkFile : IDisposable
{
    private static readonly ConcurrentDictionary<string, ChecksumCacheEntry> s_checksumCache =
        new(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly Dictionary<uint, Chunk> _chunks;

    public FileStream Stream { get; }
    public byte Version { get; }
    public byte BaseFileCount { get; }
    public uint ItemCount { get; }

    private GitChunkFile(FileStream stream, byte version, byte baseFileCount, uint itemCount, Dictionary<uint, Chunk> chunks)
    {
        Stream = stream;
        Version = version;
        BaseFileCount = baseFileCount;
        ItemCount = itemCount;
        _chunks = chunks;
    }

    public static GitChunkFile Open(
        string path,
        uint signature,
        byte expectedHashVersion,
        int headerSize,
        int itemCountOffset)
    {
        var stream = File.OpenRead(path);
        try
        {
            var hashSize = expectedHashVersion switch
            {
                1 => 20,
                2 => 32,
                _ => throw new InvalidDataException("Git chunk file hash version is invalid."),
            };

            if (!GetChecksumValidation(path, stream.Length, File.GetLastWriteTimeUtc(path).Ticks, hashSize, expectedHashVersion))
            {
                throw new InvalidDataException("Git chunk file checksum is invalid.");
            }

            if (stream.Length < headerSize ||
                ReadUInt32BigEndian(stream) != signature)
            {
                throw new InvalidDataException("Git chunk file header is invalid.");
            }

            var version = ReadByte(stream);
            if (ReadByte(stream) != expectedHashVersion)
            {
                throw new InvalidDataException("Git chunk file hash version is invalid.");
            }

            var chunkCount = ReadByte(stream);
            var baseFileCount = ReadByte(stream);
            var itemCount = 0u;
            if (itemCountOffset >= 0)
            {
                stream.Position = itemCountOffset;
                itemCount = ReadUInt32BigEndian(stream);
            }

            stream.Position = headerSize;
            var chunks = new Dictionary<uint, Chunk>();
            uint previousId = 0;
            ulong previousOffset = 0;
            for (var i = 0; i <= chunkCount; i++)
            {
                var id = ReadUInt32BigEndian(stream);
                var offset = ReadUInt64BigEndian(stream);
                if (offset > (ulong)stream.Length)
                {
                    throw new InvalidDataException("Git chunk offset is invalid.");
                }

                if (i > 0)
                {
                    if (offset < previousOffset)
                    {
                        throw new InvalidDataException("Git chunks are not ordered.");
                    }

                    chunks.Add(previousId, new Chunk((long)previousOffset, checked((long)(offset - previousOffset))));
                }

                if (i == chunkCount)
                {
                    if (id != 0)
                    {
                        throw new InvalidDataException("Git chunk table terminator is invalid.");
                    }
                }
                else if (id == 0)
                {
                    throw new InvalidDataException("Git chunk identifier is invalid.");
                }

                previousId = id;
                previousOffset = offset;
            }

            return new GitChunkFile(stream, version, baseFileCount, itemCount, chunks);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public bool TryGetChunk(uint id, out Chunk chunk)
        => _chunks.TryGetValue(id, out chunk);

    public void Dispose()
        => Stream.Dispose();

    private static bool GetChecksumValidation(string path, long length, long lastWriteTimeUtcTicks, int hashSize, byte hashVersion)
    {
        while (true)
        {
            if (s_checksumCache.TryGetValue(path, out var existing) &&
                existing.Length == length &&
                existing.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks)
            {
                return GetChecksumValidation(path, existing);
            }

            var replacement = new ChecksumCacheEntry(
                length,
                lastWriteTimeUtcTicks,
                new Lazy<bool>(
                    () => ValidateChecksum(path, hashSize, hashVersion),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            if (existing == null)
            {
                if (s_checksumCache.TryAdd(path, replacement))
                {
                    return GetChecksumValidation(path, replacement);
                }
            }
            else if (s_checksumCache.TryUpdate(path, replacement, existing))
            {
                return GetChecksumValidation(path, replacement);
            }
        }
    }

    private static bool GetChecksumValidation(string path, ChecksumCacheEntry entry)
    {
        try
        {
            return entry.IsValid.Value;
        }
        catch
        {
            ((ICollection<KeyValuePair<string, ChecksumCacheEntry>>)s_checksumCache).Remove(
                new KeyValuePair<string, ChecksumCacheEntry>(path, entry));
            throw;
        }
    }

    private static bool ValidateChecksum(string path, int hashSize, byte hashVersion)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < hashSize)
        {
            return false;
        }

        using HashAlgorithm hash = hashVersion == 1 ? SHA1.Create() : SHA256.Create();
        var remaining = stream.Length - hashSize;
        var buffer = new byte[8192];
        while (remaining > 0)
        {
            var count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            hash.TransformBlock(buffer, 0, count, buffer, 0);
            remaining -= count;
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var expected = new byte[hashSize];
        if (stream.Read(expected, 0, expected.Length) != expected.Length)
        {
            throw new EndOfStreamException();
        }

        return FixedTimeEquals(hash.Hash!, expected);
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        var result = 0;
        for (var i = 0; i < left.Length; i++)
        {
            result |= left[i] ^ right[i];
        }

        return result == 0;
    }

    internal readonly record struct Chunk(long Offset, long Length);
    private sealed record ChecksumCacheEntry(long Length, long LastWriteTimeUtcTicks, Lazy<bool> IsValid);

    internal static byte ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        return (byte)value;
    }

    internal static uint ReadUInt32BigEndian(Stream stream)
        => ((uint)ReadByte(stream) << 24) |
           ((uint)ReadByte(stream) << 16) |
           ((uint)ReadByte(stream) << 8) |
           ReadByte(stream);

    internal static ulong ReadUInt64BigEndian(Stream stream)
        => ((ulong)ReadUInt32BigEndian(stream) << 32) | ReadUInt32BigEndian(stream);
}
