// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Microsoft.Build.Tasks.Git;

/// <summary>
/// Locates packed Git objects using monolithic and incremental multi-pack-index files.
/// See https://git-scm.com/docs/gitformat-pack.html
/// </summary>
internal sealed class GitMultiPackIndexReader
{
    private const uint Signature = 0x4d494458;
    private const uint PackNamesChunk = 0x504e414d;
    private const uint OidFanoutChunk = 0x4f494446;
    private const uint OidLookupChunk = 0x4f49444c;
    private const uint ObjectOffsetsChunk = 0x4f4f4646;
    private const uint LargeOffsetsChunk = 0x4c4f4646;

    private readonly string _packDirectory;
    private readonly byte _hashVersion;
    private readonly int _hashSize;

    public GitMultiPackIndexReader(string packDirectory, ObjectNameFormat objectNameFormat)
    {
        _packDirectory = packDirectory;
        _hashVersion = objectNameFormat == ObjectNameFormat.Sha1 ? (byte)1 : (byte)2;
        _hashSize = objectNameFormat.HashSize;
    }

    public (string PackPath, long Offset)? FindObject(byte[] objectId)
    {
        foreach (var path in EnumerateIndexPaths())
        {
            try
            {
                var result = FindObject(path, objectId);
                if (result != null)
                {
                    return result;
                }
            }
            catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
            {
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateIndexPaths()
    {
        var path = Path.Combine(_packDirectory, "multi-pack-index");
        if (File.Exists(path))
        {
            yield return path;
        }

        var chainDirectory = Path.Combine(_packDirectory, "multi-pack-index.d");
        var chainPath = Path.Combine(chainDirectory, "multi-pack-index-chain");
        if (!File.Exists(chainPath))
        {
            yield break;
        }

        var hashes = new List<string>();
        foreach (var hash in File.ReadLines(chainPath))
        {
            if (IsHash(hash))
            {
                hashes.Add(hash);
            }
        }

        for (var i = hashes.Count - 1; i >= 0; i--)
        {
            var indexPath = Path.Combine(chainDirectory, $"multi-pack-index-{hashes[i]}.midx");
            if (File.Exists(indexPath))
            {
                yield return indexPath;
            }
        }
    }

    private (string PackPath, long Offset)? FindObject(string path, byte[] objectId)
    {
        using var index = GitChunkFile.Open(path, Signature, _hashVersion, headerSize: 12, itemCountOffset: 8);
        if (index.Version is not (1 or 2) ||
            !index.TryGetChunk(PackNamesChunk, out var packNames) ||
            !index.TryGetChunk(OidFanoutChunk, out var fanout) ||
            !index.TryGetChunk(OidLookupChunk, out var oidLookup) ||
            !index.TryGetChunk(ObjectOffsetsChunk, out var objectOffsets) ||
            fanout.Length != 256 * sizeof(uint))
        {
            return null;
        }

        var objectCount = ReadFanoutValue(index.Stream, fanout, 255);
        if (oidLookup.Length != (long)objectCount * _hashSize ||
            objectOffsets.Length != (long)objectCount * 2 * sizeof(uint))
        {
            return null;
        }

        var position = FindObjectPosition(index.Stream, fanout, oidLookup, objectId);
        if (position == null)
        {
            return null;
        }

        index.Stream.Position = objectOffsets.Offset + (long)position.Value * 2 * sizeof(uint);
        var packId = GitChunkFile.ReadUInt32BigEndian(index.Stream);
        var encodedOffset = GitChunkFile.ReadUInt32BigEndian(index.Stream);
        if (packId >= index.ItemCount)
        {
            return null;
        }

        long offset;
        if ((encodedOffset & 0x80000000) != 0 &&
            index.TryGetChunk(LargeOffsetsChunk, out var largeOffsets))
        {
            var largeOffsetPosition = encodedOffset & 0x7fffffff;
            if ((long)(largeOffsetPosition + 1) * sizeof(ulong) > largeOffsets.Length)
            {
                return null;
            }

            index.Stream.Position = largeOffsets.Offset + (long)largeOffsetPosition * sizeof(ulong);
            var value = GitChunkFile.ReadUInt64BigEndian(index.Stream);
            if (value > long.MaxValue)
            {
                return null;
            }

            offset = (long)value;
        }
        else
        {
            offset = encodedOffset;
        }

        var packName = ReadPackName(index.Stream, packNames, packId, index.ItemCount);
        if (packName == null || Path.GetFileName(packName) != packName)
        {
            return null;
        }

        return (Path.Combine(_packDirectory, Path.ChangeExtension(packName, ".pack")), offset);
    }

    private uint? FindObjectPosition(
        Stream stream,
        GitChunkFile.Chunk fanout,
        GitChunkFile.Chunk oidLookup,
        byte[] objectId)
    {
        var start = objectId[0] == 0 ? 0 : ReadFanoutValue(stream, fanout, objectId[0] - 1);
        var end = ReadFanoutValue(stream, fanout, objectId[0]);

        while (start < end)
        {
            var middle = start + (end - start) / 2;
            stream.Position = oidLookup.Offset + (long)middle * _hashSize;
            var comparison = CompareBytes(stream, objectId);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                start = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        return null;
    }

    private static string? ReadPackName(Stream stream, GitChunkFile.Chunk packNames, uint packId, uint packCount)
    {
        stream.Position = packNames.Offset;
        for (var i = 0u; i < packCount; i++)
        {
            var bytes = new List<byte>();
            while (stream.Position < packNames.Offset + packNames.Length)
            {
                var value = GitChunkFile.ReadByte(stream);
                if (value == 0)
                {
                    break;
                }

                bytes.Add(value);
            }

            if (i == packId)
            {
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
        }

        return null;
    }

    private static uint ReadFanoutValue(Stream stream, GitChunkFile.Chunk fanout, int index)
    {
        stream.Position = fanout.Offset + index * sizeof(uint);
        return GitChunkFile.ReadUInt32BigEndian(stream);
    }

    private static int CompareBytes(Stream stream, byte[] expected)
    {
        foreach (var value in expected)
        {
            var actual = GitChunkFile.ReadByte(stream);
            if (actual != value)
            {
                return actual.CompareTo(value);
            }
        }

        return 0;
    }

    private bool IsHash(string value)
    {
        if (value.Length != _hashSize * 2)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
