// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Build.Tasks.Git;

/// <summary>
/// Reads commit timestamps from monolithic and split Git commit-graph files.
/// See https://git-scm.com/docs/gitformat-commit-graph
/// </summary>
internal sealed class GitCommitGraphReader
{
    private const uint Signature = 0x43475048;
    private const uint OidFanoutChunk = 0x4f494446;
    private const uint OidLookupChunk = 0x4f49444c;
    private const uint CommitDataChunk = 0x43444154;

    private readonly IReadOnlyList<string> _objectDirectories;
    private readonly byte _hashVersion;
    private readonly int _hashSize;

    public GitCommitGraphReader(IReadOnlyList<string> objectDirectories, ObjectNameFormat objectNameFormat)
    {
        _objectDirectories = objectDirectories;
        _hashVersion = objectNameFormat == ObjectNameFormat.Sha1 ? (byte)1 : (byte)2;
        _hashSize = objectNameFormat.HashSize;
    }

    public DateTimeOffset? ReadCommitTimestamp(byte[] objectId)
    {
        foreach (var path in EnumerateGraphPaths())
        {
            try
            {
                var timestamp = ReadCommitTimestamp(path, objectId);
                if (timestamp != null)
                {
                    return timestamp;
                }
            }
            catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
            {
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateGraphPaths()
    {
        foreach (var objectDirectory in _objectDirectories)
        {
            var path = Path.Combine(objectDirectory, "info", "commit-graph");
            if (File.Exists(path))
            {
                yield return path;
            }
        }

        foreach (var objectDirectory in _objectDirectories)
        {
            var chainPath = Path.Combine(objectDirectory, "info", "commit-graphs", "commit-graph-chain");
            if (!File.Exists(chainPath))
            {
                continue;
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
                foreach (var graphObjectDirectory in _objectDirectories)
                {
                    var graphPath = Path.Combine(graphObjectDirectory, "info", "commit-graphs", $"graph-{hashes[i]}.graph");
                    if (File.Exists(graphPath))
                    {
                        yield return graphPath;
                        break;
                    }
                }
            }

            yield break;
        }
    }

    private DateTimeOffset? ReadCommitTimestamp(string path, byte[] objectId)
    {
        using var graph = GitChunkFile.Open(path, Signature, _hashVersion, headerSize: 8, itemCountOffset: -1);
        if (graph.Version != 1 ||
            !graph.TryGetChunk(OidFanoutChunk, out var fanout) ||
            !graph.TryGetChunk(OidLookupChunk, out var oidLookup) ||
            !graph.TryGetChunk(CommitDataChunk, out var commitData) ||
            fanout.Length != 256 * sizeof(uint))
        {
            return null;
        }

        var objectCount = ReadObjectCount(graph.Stream, fanout);
        if (oidLookup.Length != (long)objectCount * _hashSize ||
            commitData.Length != (long)objectCount * (_hashSize + 16))
        {
            return null;
        }

        var position = FindObjectPosition(graph.Stream, fanout, oidLookup, objectId);
        if (position == null)
        {
            return null;
        }

        graph.Stream.Position = commitData.Offset + (long)position.Value * (_hashSize + 16) + _hashSize + 8;
        var generationAndTimeHigh = GitChunkFile.ReadUInt32BigEndian(graph.Stream);
        var timeLow = GitChunkFile.ReadUInt32BigEndian(graph.Stream);
        var seconds = ((long)(generationAndTimeHigh & 3) << 32) | timeLow;
        return DateTimeOffset.FromUnixTimeSeconds(seconds);
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

    private static uint ReadObjectCount(Stream stream, GitChunkFile.Chunk fanout)
        => ReadFanoutValue(stream, fanout, 255);

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
