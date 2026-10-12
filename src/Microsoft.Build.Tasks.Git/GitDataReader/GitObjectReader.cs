// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Build.Tasks.Git;

/// <summary>
/// Reads commit timestamps directly from loose or packed Git objects without invoking Git.
/// See https://git-scm.com/docs/gitformat-pack.html
/// </summary>
internal sealed class GitObjectReader
{
    private const int MaxDeltaDepth = 4095;
    private const int MaxObjectSize = 16 * 1024 * 1024;
    private const uint PackIndexSignature = 0xff744f63;

    private readonly string _primaryObjectsDirectory;
    private readonly ObjectNameFormat _objectNameFormat;
    private readonly int _hashSize;
    private readonly bool _useCommitGraph;
    private readonly bool _useMultiPackIndex;

    public GitObjectReader(
        string commonDirectory,
        ObjectNameFormat objectNameFormat,
        bool useCommitGraph = true,
        bool useMultiPackIndex = true)
    {
        _primaryObjectsDirectory = Path.Combine(commonDirectory, "objects");
        _objectNameFormat = objectNameFormat;
        _hashSize = objectNameFormat.HashSize;
        _useCommitGraph = useCommitGraph;
        _useMultiPackIndex = useMultiPackIndex;
    }

    public DateTimeOffset? ReadCommitTimestamp(string objectId)
    {
        if (!TryParseObjectId(objectId, out var objectIdBytes))
        {
            return null;
        }

        var objectDirectories = EnumerateObjectDirectories().ToArray();
        if (_useCommitGraph)
        {
            var commitGraphTimestamp = new GitCommitGraphReader(objectDirectories, _objectNameFormat).ReadCommitTimestamp(objectIdBytes);
            if (commitGraphTimestamp != null)
            {
                return commitGraphTimestamp;
            }
        }

        var gitObject = ReadObject(objectDirectories, objectIdBytes, recursionDepth: 0);
        if (gitObject is not GitObject { Type: GitObjectType.Commit } commit)
        {
            return null;
        }

        return ParseCommitTimestamp(commit.Data);
    }

    private GitObject? ReadObject(IReadOnlyList<string> objectDirectories, byte[] objectId, int recursionDepth)
    {
        if (recursionDepth > MaxDeltaDepth)
        {
            throw new InvalidDataException("Git object delta chain is too deep.");
        }

        foreach (var objectsDirectory in objectDirectories)
        {
            try
            {
                var looseObject = ReadLooseObject(objectsDirectory, objectId);
                if (looseObject != null && ObjectIdMatches(looseObject.Value, objectId))
                {
                    return looseObject;
                }
            }
            catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
            {
            }

            try
            {
                var packedObject = ReadPackedObject(objectDirectories, objectsDirectory, objectId, recursionDepth);
                if (packedObject != null)
                {
                    return packedObject;
                }
            }
            catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
            {
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateObjectDirectories()
    {
        var comparer = Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var visited = new HashSet<string>(comparer);
        var pending = new Queue<string>();
        pending.Enqueue(_primaryObjectsDirectory);

        while (pending.Count > 0)
        {
            string objectsDirectory;
            try
            {
                objectsDirectory = Path.GetFullPath(pending.Dequeue());
            }
            catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
            {
                continue;
            }

            if (!visited.Add(objectsDirectory))
            {
                continue;
            }

            yield return objectsDirectory;

            var alternatesPath = Path.Combine(objectsDirectory, "info", "alternates");
            if (!File.Exists(alternatesPath))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(alternatesPath);
            }
            catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (!TryParseAlternatePath(line, out var alternatePath))
                {
                    continue;
                }

                try
                {
                    pending.Enqueue(
                        Path.GetFullPath(
                            Path.IsPathRooted(alternatePath)
                                ? alternatePath
                                : Path.Combine(objectsDirectory, alternatePath)));
                }
                catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
                {
                }
            }
        }
    }

    private static bool TryParseAlternatePath(string line, out string path)
    {
        path = "";
        if (line.Length == 0 || line[0] == '#')
        {
            return false;
        }

        if (line[0] != '"')
        {
            path = line;
            return true;
        }

        var bytes = new List<byte>();
        var position = 1;
        while (position < line.Length)
        {
            if (line[position] == '"')
            {
                if (position != line.Length - 1)
                {
                    return false;
                }

                try
                {
                    path = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                        .GetString(bytes.ToArray());
                    return path.Length > 0;
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
            }

            if (line[position] != '\\')
            {
                var start = position;
                while (position < line.Length && line[position] != '\\' && line[position] != '"')
                {
                    position++;
                }

                bytes.AddRange(Encoding.UTF8.GetBytes(line.Substring(start, position - start)));
                continue;
            }

            position++;
            if (position == line.Length)
            {
                return false;
            }

            var value = line[position++] switch
            {
                'a' => (byte)'\a',
                'b' => (byte)'\b',
                't' => (byte)'\t',
                'n' => (byte)'\n',
                'v' => (byte)'\v',
                'f' => (byte)'\f',
                'r' => (byte)'\r',
                '\\' => (byte)'\\',
                '"' => (byte)'"',
                var digit when digit is >= '0' and <= '7' => ParseOctalByte(line, ref position, digit),
                _ => -1,
            };

            if (value is < 0 or > byte.MaxValue)
            {
                return false;
            }

            bytes.Add((byte)value);
        }

        return false;
    }

    private static int ParseOctalByte(string value, ref int position, char firstDigit)
    {
        var result = firstDigit - '0';
        for (var i = 1; i < 3 && position < value.Length && value[position] is >= '0' and <= '7'; i++)
        {
            result = result * 8 + value[position++] - '0';
        }

        return result;
    }

    private GitObject? ReadLooseObject(string objectsDirectory, byte[] objectId)
    {
        var objectIdHex = ToHexString(objectId);
        var path = Path.Combine(objectsDirectory, objectIdHex[..2], objectIdHex[2..]);
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var data = Inflate(stream);
        var headerEnd = Array.IndexOf(data, (byte)0);
        if (headerEnd < 0)
        {
            throw new InvalidDataException("Git object header is invalid.");
        }

        var header = Encoding.ASCII.GetString(data, 0, headerEnd);
        var separator = header.IndexOf(' ');
        if (separator < 0 ||
            !TryParseObjectType(header[..separator], out var type) ||
            !int.TryParse(header[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size < 0 ||
            size != data.Length - headerEnd - 1)
        {
            throw new InvalidDataException("Git object header is invalid.");
        }

        var content = new byte[size];
        Buffer.BlockCopy(data, headerEnd + 1, content, 0, size);
        return new GitObject(type, content);
    }

    private GitObject? ReadPackedObject(
        IReadOnlyList<string> objectDirectories,
        string objectsDirectory,
        byte[] objectId,
        int recursionDepth)
    {
        var packDirectory = Path.Combine(objectsDirectory, "pack");
        if (!Directory.Exists(packDirectory))
        {
            return null;
        }

        if (_useMultiPackIndex)
        {
            var multiPackIndexObject = new GitMultiPackIndexReader(packDirectory, _objectNameFormat).FindObject(objectId);
            if (multiPackIndexObject != null && File.Exists(multiPackIndexObject.Value.PackPath))
            {
                try
                {
                    var gitObject = ReadPackedObjectAt(
                        objectDirectories,
                        multiPackIndexObject.Value.PackPath,
                        multiPackIndexObject.Value.Offset,
                        recursionDepth);
                    if (ObjectIdMatches(gitObject, objectId))
                    {
                        return gitObject;
                    }
                }
                catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
                {
                }
            }
        }

        foreach (var indexPath in Directory.EnumerateFiles(packDirectory, "pack-*.idx"))
        {
            try
            {
                var offset = FindObjectOffset(indexPath, objectId);
                if (offset != null)
                {
                    var gitObject = ReadPackedObjectAt(
                        objectDirectories,
                        Path.ChangeExtension(indexPath, ".pack"),
                        offset.Value,
                        recursionDepth);
                    if (ObjectIdMatches(gitObject, objectId))
                    {
                        return gitObject;
                    }
                }
            }
            catch (Exception e) when (GitRepository.IsTimestampUnavailableException(e))
            {
            }
        }

        return null;
    }

    private bool ObjectIdMatches(GitObject gitObject, byte[] expectedObjectId)
    {
        var type = gitObject.Type switch
        {
            GitObjectType.Commit => "commit",
            GitObjectType.Tree => "tree",
            GitObjectType.Blob => "blob",
            GitObjectType.Tag => "tag",
            _ => null,
        };
        if (type == null)
        {
            return false;
        }

        var header = Encoding.ASCII.GetBytes(
            type + " " + gitObject.Data.Length.ToString(CultureInfo.InvariantCulture) + "\0");
        using HashAlgorithm hash = _objectNameFormat == ObjectNameFormat.Sha1 ? SHA1.Create() : SHA256.Create();
        hash.TransformBlock(header, 0, header.Length, header, 0);
        hash.TransformFinalBlock(gitObject.Data, 0, gitObject.Data.Length);

        var actualObjectId = hash.Hash!;
        var difference = actualObjectId.Length ^ expectedObjectId.Length;
        for (var i = 0; i < Math.Min(actualObjectId.Length, expectedObjectId.Length); i++)
        {
            difference |= actualObjectId[i] ^ expectedObjectId[i];
        }

        return difference == 0;
    }

    private long? FindObjectOffset(string indexPath, byte[] objectId)
    {
        using var stream = File.OpenRead(indexPath);
        var signatureOrFirstFanoutValue = ReadUInt32BigEndian(stream);
        if (signatureOrFirstFanoutValue == PackIndexSignature)
        {
            return ReadVersion2Index(stream, objectId);
        }

        return _objectNameFormat == ObjectNameFormat.Sha1
            ? ReadVersion1Index(stream, signatureOrFirstFanoutValue, objectId)
            : null;
    }

    private long? ReadVersion1Index(Stream stream, uint firstFanoutValue, byte[] objectId)
    {
        var (bucketStart, bucketEnd, _) = ReadFanout(
            stream,
            fanoutPosition: 0,
            firstFanoutValue,
            objectId[0]);
        const int entriesPosition = 256 * sizeof(uint);
        var entrySize = sizeof(uint) + _hashSize;
        var position = FindObjectPosition(
            stream,
            entriesPosition,
            entrySize,
            sizeof(uint),
            bucketStart,
            bucketEnd,
            objectId);
        if (position != null)
        {
            stream.Position = entriesPosition + (long)position.Value * entrySize;
            return ReadUInt32BigEndian(stream);
        }

        return null;
    }

    private long? ReadVersion2Index(Stream stream, byte[] objectId)
    {
        if (ReadUInt32BigEndian(stream) != 2)
        {
            return null;
        }

        const int fanoutPosition = 2 * sizeof(uint);
        var (bucketStart, bucketEnd, objectCount) = ReadFanout(
            stream,
            fanoutPosition,
            firstFanoutValue: null,
            objectId[0]);

        const int headerAndFanoutSize = fanoutPosition + 256 * sizeof(uint);
        var objectIdsPosition = headerAndFanoutSize;
        var position = FindObjectPosition(
            stream,
            objectIdsPosition,
            _hashSize,
            0,
            bucketStart,
            bucketEnd,
            objectId);
        if (position == null)
        {
            return null;
        }

        var offsetsPosition = objectIdsPosition + (long)objectCount * _hashSize + (long)objectCount * sizeof(uint);
        stream.Position = offsetsPosition + (long)position.Value * sizeof(uint);
        var offset = ReadUInt32BigEndian(stream);
        if ((offset & 0x80000000) == 0)
        {
            return offset;
        }

        var largeOffsetPosition =
            offsetsPosition +
            (long)objectCount * sizeof(uint) +
            (long)(offset & 0x7fffffff) * sizeof(ulong);
        var trailerSize = _hashSize * 2;
        if (stream.Length < trailerSize ||
            largeOffsetPosition > stream.Length - trailerSize - sizeof(ulong))
        {
            throw new InvalidDataException("Git pack large object offset is invalid.");
        }

        stream.Position = largeOffsetPosition;
        var largeOffset = ReadUInt64BigEndian(stream);
        if (largeOffset > long.MaxValue)
        {
            throw new InvalidDataException("Git pack object offset is too large.");
        }

        return (long)largeOffset;
    }

    private static uint? FindObjectPosition(
        Stream stream,
        long entriesPosition,
        int entrySize,
        int objectIdOffset,
        uint bucketStart,
        uint bucketEnd,
        byte[] objectId)
    {
        while (bucketStart < bucketEnd)
        {
            var middle = bucketStart + (bucketEnd - bucketStart) / 2;
            stream.Position = entriesPosition + (long)middle * entrySize + objectIdOffset;
            var comparison = CompareBytes(stream, objectId);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                bucketStart = middle + 1;
            }
            else
            {
                bucketEnd = middle;
            }
        }

        return null;
    }

    private static (uint BucketStart, uint BucketEnd, uint ObjectCount) ReadFanout(
        Stream stream,
        int fanoutPosition,
        uint? firstFanoutValue,
        byte firstObjectIdByte)
    {
        stream.Position = fanoutPosition + (firstObjectIdByte == 0 ? 0 : (firstObjectIdByte - 1) * sizeof(uint));
        var bucketStart = firstObjectIdByte == 0 ? 0 : ReadUInt32BigEndian(stream);
        var bucketEnd = ReadUInt32BigEndian(stream);

        if (firstObjectIdByte == 0 && firstFanoutValue != null)
        {
            bucketEnd = firstFanoutValue.Value;
        }

        stream.Position = fanoutPosition + 255 * sizeof(uint);
        var objectCount = ReadUInt32BigEndian(stream);
        return (bucketStart, bucketEnd, objectCount);
    }

    private GitObject ReadPackedObjectAt(
        IReadOnlyList<string> objectDirectories,
        string packPath,
        long objectOffset,
        int recursionDepth)
    {
        var deltas = new List<byte[]>();
        while (true)
        {
            if (recursionDepth + deltas.Count > MaxDeltaDepth)
            {
                throw new InvalidDataException("Git object delta chain is too deep.");
            }

            var (type, data, baseOffset, baseObjectId) = ReadPackedObjectData(packPath, objectOffset);

            if (baseOffset != null)
            {
                deltas.Add(data);
                objectOffset = baseOffset.Value;
                continue;
            }

            GitObject gitObject;
            if (baseObjectId != null)
            {
                deltas.Add(data);
                gitObject = ReadObject(objectDirectories, baseObjectId, recursionDepth + deltas.Count) ??
                    throw new InvalidDataException("Git pack delta base object is missing.");
            }
            else
            {
                if (type is not (GitObjectType.Commit or GitObjectType.Tree or GitObjectType.Blob or GitObjectType.Tag))
                {
                    throw new InvalidDataException("Git pack object type is invalid.");
                }

                gitObject = new GitObject(type, data);
            }

            for (var i = deltas.Count - 1; i >= 0; i--)
            {
                gitObject = new GitObject(gitObject.Type, ApplyDelta(gitObject.Data, deltas[i]));
            }

            return gitObject;
        }
    }

    private (GitObjectType Type, byte[] Data, long? BaseOffset, byte[]? BaseObjectId) ReadPackedObjectData(
        string packPath,
        long objectOffset)
    {
        using var stream = File.OpenRead(packPath);
        ValidatePackHeader(stream);
        stream.Position = objectOffset;

        var header = ReadByte(stream);
        var type = (GitObjectType)((header >> 4) & 7);
        var size = header & 0x0f;
        var shift = 4;
        while ((header & 0x80) != 0)
        {
            header = ReadByte(stream);
            size |= (header & 0x7f) << shift;
            shift += 7;
            if (shift > 28)
            {
                throw new InvalidDataException("Git pack object is too large.");
            }
        }

        long? baseOffset = null;
        byte[]? baseObjectId = null;
        if (type == GitObjectType.OffsetDelta)
        {
            var value = ReadByte(stream);
            long distance = value & 0x7f;
            while ((value & 0x80) != 0)
            {
                value = ReadByte(stream);
                distance = ((distance + 1) << 7) | (uint)(value & 0x7f);
            }

            if (distance <= 0 || distance >= objectOffset)
            {
                throw new InvalidDataException("Git pack delta base offset is invalid.");
            }

            baseOffset = objectOffset - distance;
        }
        else if (type == GitObjectType.ReferenceDelta)
        {
            baseObjectId = ReadBytes(stream, _hashSize);
        }

        var data = Inflate(stream);
        if (data.Length != size)
        {
            throw new InvalidDataException("Git pack object size is invalid.");
        }

        return (type, data, baseOffset, baseObjectId);
    }

    internal static byte[] ApplyDelta(byte[] source, byte[] delta)
    {
        var position = 0;
        var sourceSize = ReadVariableLengthInteger(delta, ref position);
        var resultSize = ReadVariableLengthInteger(delta, ref position);
        if (sourceSize != source.Length || resultSize < 0 || resultSize > MaxObjectSize)
        {
            throw new InvalidDataException("Git pack delta header is invalid.");
        }

        var result = new byte[resultSize];
        var resultPosition = 0;
        while (position < delta.Length)
        {
            var command = delta[position++];
            if ((command & 0x80) != 0)
            {
                var copyOffset = 0;
                var copySize = 0;
                for (var i = 0; i < 4; i++)
                {
                    if ((command & (1 << i)) != 0)
                    {
                        copyOffset |= ReadDeltaByte(delta, ref position) << (i * 8);
                    }
                }

                for (var i = 0; i < 3; i++)
                {
                    if ((command & (1 << (i + 4))) != 0)
                    {
                        copySize |= ReadDeltaByte(delta, ref position) << (i * 8);
                    }
                }

                if (copySize == 0)
                {
                    copySize = 0x10000;
                }

                if (copyOffset < 0 ||
                    copySize < 0 ||
                    (long)copyOffset + copySize > source.Length ||
                    (long)resultPosition + copySize > result.Length)
                {
                    throw new InvalidDataException("Git pack delta copy instruction is invalid.");
                }

                Buffer.BlockCopy(source, copyOffset, result, resultPosition, copySize);
                resultPosition += copySize;
            }
            else
            {
                if (command == 0 || position + command > delta.Length || resultPosition + command > result.Length)
                {
                    throw new InvalidDataException("Git pack delta insert instruction is invalid.");
                }

                Buffer.BlockCopy(delta, position, result, resultPosition, command);
                position += command;
                resultPosition += command;
            }
        }

        if (resultPosition != result.Length)
        {
            throw new InvalidDataException("Git pack delta result size is invalid.");
        }

        return result;
    }

    private static int ReadVariableLengthInteger(byte[] data, ref int position)
    {
        uint result = 0;
        var shift = 0;
        byte value;
        do
        {
            if (position >= data.Length || shift > 28)
            {
                throw new InvalidDataException("Git pack delta header is invalid.");
            }

            value = data[position++];
            var chunk = (uint)(value & 0x7f);
            if (shift == 28 && chunk > 7)
            {
                throw new InvalidDataException("Git pack delta header is invalid.");
            }

            result |= chunk << shift;
            shift += 7;
        }
        while ((value & 0x80) != 0);

        return (int)result;
    }

    private static byte ReadDeltaByte(byte[] data, ref int position)
    {
        if (position >= data.Length)
        {
            throw new InvalidDataException("Git pack delta instruction is invalid.");
        }

        return data[position++];
    }

    private static DateTimeOffset? ParseCommitTimestamp(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        foreach (var untrimmedLine in text.Split('\n'))
        {
            var line = untrimmedLine.TrimEnd(' ', '\t', '\r');
            if (line.Length == 0)
            {
                break;
            }

            if (!line.StartsWith("committer ", StringComparison.Ordinal))
            {
                continue;
            }

            var timeZoneSeparator = line.LastIndexOf(' ');
            var timestampSeparator = timeZoneSeparator > 0 ? line.LastIndexOf(' ', timeZoneSeparator - 1) : -1;
            if (timestampSeparator < 0 ||
                !IsGitTimeZone(line, timeZoneSeparator + 1) ||
                !long.TryParse(
                    line.Substring(timestampSeparator + 1, timeZoneSeparator - timestampSeparator - 1),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var timestamp))
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsGitTimeZone(string line, int start)
        => line.Length - start == 5 &&
           line[start] is '+' or '-' &&
           char.IsDigit(line[start + 1]) &&
           char.IsDigit(line[start + 2]) &&
           char.IsDigit(line[start + 3]) &&
           char.IsDigit(line[start + 4]);

    private static byte[] Inflate(Stream stream)
    {
        var compressionMethodAndFlags = ReadByte(stream);
        var flags = ReadByte(stream);
        if ((compressionMethodAndFlags & 0x0f) != 8 || ((compressionMethodAndFlags << 8) + flags) % 31 != 0)
        {
            throw new InvalidDataException("Git object compression header is invalid.");
        }

        if ((flags & 0x20) != 0)
        {
            ReadBytes(stream, sizeof(uint));
        }

        using var compressedStream = new SingleByteReadStream(stream);
        using var inflater = new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        var adlerA = 1u;
        var adlerB = 0u;
        int count;
        while ((count = inflater.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > MaxObjectSize)
            {
                throw new InvalidDataException("Git object is too large.");
            }

            output.Write(buffer, 0, count);
            for (var i = 0; i < count; i++)
            {
                adlerA = (adlerA + buffer[i]) % 65521;
                adlerB = (adlerB + adlerA) % 65521;
            }
        }

        var expectedAdler32 = ReadUInt32BigEndian(stream);
        var actualAdler32 = (adlerB << 16) | adlerA;
        if (actualAdler32 != expectedAdler32)
        {
            throw new InvalidDataException("Git object compression checksum is invalid.");
        }

        return output.ToArray();
    }

    private static void ValidatePackHeader(Stream stream)
    {
        var signature = ReadBytes(stream, 4);
        if (signature[0] != 'P' || signature[1] != 'A' || signature[2] != 'C' || signature[3] != 'K')
        {
            throw new InvalidDataException("Git pack header is invalid.");
        }

        var version = ReadUInt32BigEndian(stream);
        if (version is not (2 or 3))
        {
            throw new InvalidDataException("Git pack version is not supported.");
        }

        _ = ReadUInt32BigEndian(stream);
    }

    private bool TryParseObjectId(string value, out byte[] result)
    {
        result = Array.Empty<byte>();
        if (value.Length != _hashSize * 2)
        {
            return false;
        }

        result = new byte[_hashSize];
        for (var i = 0; i < result.Length; i++)
        {
            var high = ParseHexDigit(value[i * 2]);
            var low = ParseHexDigit(value[i * 2 + 1]);
            if (high < 0 || low < 0)
            {
                result = Array.Empty<byte>();
                return false;
            }

            result[i] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static bool TryParseObjectType(string value, out GitObjectType type)
    {
        type = value switch
        {
            "commit" => GitObjectType.Commit,
            "tree" => GitObjectType.Tree,
            "blob" => GitObjectType.Blob,
            "tag" => GitObjectType.Tag,
            _ => GitObjectType.None,
        };

        return type != GitObjectType.None;
    }

    private static int ParseHexDigit(char value)
        => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };

    private static string ToHexString(byte[] value)
    {
        const string digits = "0123456789abcdef";
        var result = new char[value.Length * 2];
        for (var i = 0; i < value.Length; i++)
        {
            result[i * 2] = digits[value[i] >> 4];
            result[i * 2 + 1] = digits[value[i] & 0x0f];
        }

        return new string(result);
    }

    private static int CompareBytes(Stream stream, byte[] expected)
    {
        foreach (var value in expected)
        {
            var actual = ReadByte(stream);
            if (actual != value)
            {
                return actual.CompareTo(value);
            }
        }

        return 0;
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var bytesRead = stream.Read(result, offset, count - offset);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException();
            }

            offset += bytesRead;
        }

        return result;
    }

    private static byte ReadByte(Stream stream)
    {
        var result = stream.ReadByte();
        if (result < 0)
        {
            throw new EndOfStreamException();
        }

        return (byte)result;
    }

    private static uint ReadUInt32BigEndian(Stream stream)
        => ((uint)ReadByte(stream) << 24) |
           ((uint)ReadByte(stream) << 16) |
           ((uint)ReadByte(stream) << 8) |
           ReadByte(stream);

    private static ulong ReadUInt64BigEndian(Stream stream)
        => ((ulong)ReadUInt32BigEndian(stream) << 32) | ReadUInt32BigEndian(stream);

    private sealed class SingleByteReadStream : Stream
    {
        private readonly Stream _stream;

        public SingleByteReadStream(Stream stream)
        {
            _stream = stream;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _stream.Read(buffer, offset, Math.Min(count, 1));

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private readonly record struct GitObject(GitObjectType Type, byte[] Data);

    private enum GitObjectType
    {
        None,
        Commit,
        Tree,
        Blob,
        Tag,
        OffsetDelta = 6,
        ReferenceDelta,
    }
}
