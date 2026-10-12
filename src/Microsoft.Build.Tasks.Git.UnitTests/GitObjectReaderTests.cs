// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the License.txt file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TestUtilities;
using Xunit;

namespace Microsoft.Build.Tasks.Git.UnitTests;

public class GitObjectReaderTests
{
    [Fact]
    public void ReadCommitTimestamp_LooseObject()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0230\n",
            packed: false);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void ReadCommitTimestamp_PackedDeltaObject(int indexVersion, bool offsetDelta)
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 -0800\n",
            packed: true,
            offsetDelta,
            indexVersion);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_DeepOffsetDeltaChain()
    {
        using var temp = new TempRoot();
        var gitDirectory = temp.CreateDirectory();
        gitDirectory.CreateFile("config").WriteAllText("");
        var objectId = GitObjectTestUtilities.WriteOffsetDeltaChain(
            gitDirectory.Path,
            depth: 65,
            timestamp: 1735734896);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_MissingObject()
    {
        using var temp = new TempRoot();
        var gitDirectory = temp.CreateDirectory();

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Null(reader.ReadCommitTimestamp(new string('0', 40)));
    }

    [Fact]
    public void ReadCommitTimestamp_AlternateObjectDirectory()
    {
        using var temp = new TempRoot();
        var (_, sourceGitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        var sharedGitDirectory = temp.CreateDirectory();
        var alternatesDirectory = Directory.CreateDirectory(Path.Combine(sharedGitDirectory.Path, "objects", "info"));
        File.WriteAllText(
            Path.Combine(alternatesDirectory.FullName, "alternates"),
            Path.Combine(sourceGitDirectory.Path, "objects") + Environment.NewLine);

        var reader = new GitObjectReader(sharedGitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [ConditionalFact(typeof(WindowsOnly))]
    public void ReadCommitTimestamp_UnreadableAlternatesFileUsesPrimaryObjectDirectory()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        var infoDirectory = Directory.CreateDirectory(Path.Combine(gitDirectory.Path, "objects", "info"));
        var alternatesPath = Path.Combine(infoDirectory.FullName, "alternates");
        File.WriteAllText(alternatesPath, "");
        using var lockedAlternates = File.Open(alternatesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCommitTimestamp_QuotedAlternateObjectDirectory(bool relative)
    {
        using var temp = new TempRoot();
        var (_, sourceGitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        var sharedGitDirectory = temp.CreateDirectory();
        var sharedObjectsDirectory = Directory.CreateDirectory(Path.Combine(sharedGitDirectory.Path, "objects"));
        var alternatesDirectory = Directory.CreateDirectory(Path.Combine(sharedObjectsDirectory.FullName, "info"));
        var alternatePath = Path.Combine(sourceGitDirectory.Path, "objects");
        if (relative)
        {
            var relocatedPath = Path.Combine(sharedGitDirectory.Path, "alternate objects");
            Directory.Move(alternatePath, relocatedPath);
            alternatePath = "../alternate objects";
        }

        var quotedPath = "\"" +
            alternatePath.Replace("\\", "\\\\").Replace(" ", "\\040") +
            "\"";
        File.WriteAllLines(
            Path.Combine(alternatesDirectory.FullName, "alternates"),
            new[] { "# ignored", "\"unterminated", "\"bad\\escape\"", quotedPath });

        var reader = new GitObjectReader(sharedGitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_TrailingCommitterWhitespace()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000 \n",
            packed: false);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void GetHeadCommitTimestamp_InvalidObjectIsUnavailable()
    {
        using var temp = new TempRoot();
        var (repositoryDirectory, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        File.WriteAllText(Path.Combine(gitDirectory.Path, "objects", objectId[..2], objectId[2..]), "invalid");

        using var repository = GitRepository.OpenRepository(repositoryDirectory.Path, GitEnvironment.Empty);

        Assert.Null(repository!.GetHeadCommitTimestamp());
    }

    [Fact]
    public void GetHeadCommitTimestamp_InvalidCompressionChecksumIsUnavailable()
    {
        using var temp = new TempRoot();
        var (repositoryDirectory, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        var objectPath = Path.Combine(gitDirectory.Path, "objects", objectId[..2], objectId[2..]);
        CorruptLastByte(objectPath);

        using var repository = GitRepository.OpenRepository(repositoryDirectory.Path, GitEnvironment.Empty);

        Assert.Null(repository!.GetHeadCommitTimestamp());
    }

    [Fact]
    public void ReadCommitTimestamp_CorruptLooseObjectFallsBackToPack()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        var looseObjectDirectory = Directory.CreateDirectory(Path.Combine(gitDirectory.Path, "objects", objectId[..2]));
        File.WriteAllText(Path.Combine(looseObjectDirectory.FullName, objectId[2..]), "invalid");

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_LooseObjectIdMismatch()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        var (_, otherGitDirectory, otherObjectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 946684800 +0000\n",
            packed: false);
        File.Copy(
            Path.Combine(otherGitDirectory.Path, "objects", otherObjectId[..2], otherObjectId[2..]),
            Path.Combine(gitDirectory.Path, "objects", objectId[..2], objectId[2..]),
            overwrite: true);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_PackIndexOffsetObjectIdMismatch()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        GitObjectTestUtilities.PointPackIndexAtOtherObject(gitDirectory.Path, objectId);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_CorruptPackSourcesFallBackToValidPack()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        var packDirectory = Path.Combine(gitDirectory.Path, "objects", "pack");
        var packPath = Directory.EnumerateFiles(packDirectory, "pack-*.pack").Single();
        var indexPath = Path.ChangeExtension(packPath, ".idx");
        GitObjectTestUtilities.WriteMultiPackIndex(gitDirectory.Path, incremental: false);
        File.Copy(packPath, Path.Combine(packDirectory, "pack-valid.pack"));
        File.Copy(indexPath, Path.Combine(packDirectory, "pack-valid.idx"));
        File.WriteAllText(packPath, "invalid");
        File.WriteAllText(indexPath, "invalid");

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_CorruptObjectDirectoryFallsBackToAlternate()
    {
        using var temp = new TempRoot();
        var (_, sourceGitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        var sharedGitDirectory = temp.CreateDirectory();
        var localPackDirectory = Directory.CreateDirectory(Path.Combine(sharedGitDirectory.Path, "objects", "pack"));
        File.WriteAllText(Path.Combine(localPackDirectory.FullName, "pack-invalid.idx"), "invalid");
        var alternatesDirectory = Directory.CreateDirectory(Path.Combine(sharedGitDirectory.Path, "objects", "info"));
        File.WriteAllText(
            Path.Combine(alternatesDirectory.FullName, "alternates"),
            Path.Combine(sourceGitDirectory.Path, "objects") + Environment.NewLine);

        var reader = new GitObjectReader(sharedGitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCommitTimestamp_CommitGraph(bool split)
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        Directory.Delete(Path.Combine(gitDirectory.Path, "objects", objectId[..2]), recursive: true);
        GitObjectTestUtilities.WriteCommitGraph(gitDirectory.Path, objectId, 1735734896, split);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_InvalidCommitGraphFallsBack()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        var infoDirectory = Directory.CreateDirectory(Path.Combine(gitDirectory.Path, "objects", "info"));
        File.WriteAllText(Path.Combine(infoDirectory.FullName, "commit-graph"), "invalid");

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_InvalidCommitGraphChecksum()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        Directory.Delete(Path.Combine(gitDirectory.Path, "objects", objectId[..2]), recursive: true);
        GitObjectTestUtilities.WriteCommitGraph(gitDirectory.Path, objectId, 1735734896, split: false);
        var graphPath = Path.Combine(gitDirectory.Path, "objects", "info", "commit-graph");
        CorruptLastByte(graphPath);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_DisabledCommitGraph()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false);
        Directory.Delete(Path.Combine(gitDirectory.Path, "objects", objectId[..2]), recursive: true);
        GitObjectTestUtilities.WriteCommitGraph(gitDirectory.Path, objectId, 1735734896, split: false);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1, useCommitGraph: false);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCommitTimestamp_MultiPackIndex(bool incremental)
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        GitObjectTestUtilities.WriteMultiPackIndex(gitDirectory.Path, incremental);
        foreach (var indexPath in Directory.EnumerateFiles(Path.Combine(gitDirectory.Path, "objects", "pack"), "pack-*.idx"))
        {
            File.Delete(indexPath);
        }

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_Sha256PackIndex()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true,
            objectNameFormat: ObjectNameFormat.Sha256);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha256);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_Sha256RejectsPackIndexVersion1()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true,
            indexVersion: 1,
            objectNameFormat: ObjectNameFormat.Sha256);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha256);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_LargeOffsetCannotUseIndexTrailer()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        GitObjectTestUtilities.MovePackOffsetIntoIndexTrailer(gitDirectory.Path, objectId);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_Sha256CommitGraph()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: false,
            objectNameFormat: ObjectNameFormat.Sha256);
        Directory.Delete(Path.Combine(gitDirectory.Path, "objects", objectId[..2]), recursive: true);
        GitObjectTestUtilities.WriteCommitGraph(
            gitDirectory.Path,
            objectId,
            1735734896,
            split: false,
            objectNameFormat: ObjectNameFormat.Sha256);

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha256);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_Sha256MultiPackIndex()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true,
            objectNameFormat: ObjectNameFormat.Sha256);
        GitObjectTestUtilities.WriteMultiPackIndex(
            gitDirectory.Path,
            incremental: false,
            objectNameFormat: ObjectNameFormat.Sha256);
        foreach (var indexPath in Directory.EnumerateFiles(Path.Combine(gitDirectory.Path, "objects", "pack"), "pack-*.idx"))
        {
            File.Delete(indexPath);
        }

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha256);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void MultiPackIndex_HighDirectOffsetWithoutLargeOffsets()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        GitObjectTestUtilities.WriteMultiPackIndex(gitDirectory.Path, incremental: false, encodedOffset: 0x90000000);

        var packDirectory = Path.Combine(gitDirectory.Path, "objects", "pack");
        var reader = new GitMultiPackIndexReader(packDirectory, ObjectNameFormat.Sha1);
        var result = reader.FindObject(GitObjectTestUtilities.ParseObjectId(objectId));

        Assert.NotNull(result);
        Assert.Equal(0x90000000L, result.Value.Offset);
    }

    [Fact]
    public void ReadCommitTimestamp_InvalidMultiPackIndexFallsBack()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        File.WriteAllText(Path.Combine(gitDirectory.Path, "objects", "pack", "multi-pack-index"), "invalid");

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 34, 56, TimeSpan.Zero), reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_InvalidMultiPackIndexChecksum()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        GitObjectTestUtilities.WriteMultiPackIndex(gitDirectory.Path, incremental: false);
        var packDirectory = Path.Combine(gitDirectory.Path, "objects", "pack");
        foreach (var indexPath in Directory.EnumerateFiles(packDirectory, "pack-*.idx"))
        {
            File.Delete(indexPath);
        }

        CorruptLastByte(Path.Combine(packDirectory, "multi-pack-index"));

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Fact]
    public void ReadCommitTimestamp_DisabledMultiPackIndex()
    {
        using var temp = new TempRoot();
        var (_, gitDirectory, objectId) = GitObjectTestUtilities.CreateRepositoryWithCommit(
            temp,
            "committer Test <test@example.com> 1735734896 +0000\n",
            packed: true);
        GitObjectTestUtilities.WriteMultiPackIndex(gitDirectory.Path, incremental: false);
        foreach (var indexPath in Directory.EnumerateFiles(Path.Combine(gitDirectory.Path, "objects", "pack"), "pack-*.idx"))
        {
            File.Delete(indexPath);
        }

        var reader = new GitObjectReader(gitDirectory.Path, ObjectNameFormat.Sha1, useMultiPackIndex: false);

        Assert.Null(reader.ReadCommitTimestamp(objectId));
    }

    [Theory]
    [MemberData(nameof(InvalidDeltas))]
    public void ApplyDelta_InvalidData(byte[] delta)
    {
        Assert.Throws<InvalidDataException>(() => GitObjectReader.ApplyDelta(new byte[] { 0 }, delta));
    }

    public static IEnumerable<object[]> InvalidDeltas()
    {
        yield return new object[] { new byte[] { 0x01, 0x01, 0x81 } };
        yield return new object[] { new byte[] { 0x01, 0x80, 0x80, 0x80, 0x80, 0x08 } };
        yield return new object[] { new byte[] { 0x01, 0x01, 0x91, 0xff, 0x02 } };
    }

    private static void CorruptLastByte(string path)
    {
        var data = File.ReadAllBytes(path);
        data[data.Length - 1] ^= 1;
        File.WriteAllBytes(path, data);
    }
}

internal static class GitObjectTestUtilities
{
    private const uint OidFanoutChunk = 0x4f494446;
    private const uint OidLookupChunk = 0x4f49444c;
    private const uint CommitDataChunk = 0x43444154;
    private const uint PackNamesChunk = 0x504e414d;
    private const uint ObjectOffsetsChunk = 0x4f4f4646;

    public static (TempDirectory RepositoryDirectory, TempDirectory GitDirectory, string ObjectId) CreateRepositoryWithCommit(
        TempRoot temp,
        string committerLine,
        bool packed,
        bool offsetDelta = false,
        int indexVersion = 2,
        ObjectNameFormat objectNameFormat = ObjectNameFormat.Sha1)
    {
        var repositoryDirectory = temp.CreateDirectory();
        var gitDirectory = repositoryDirectory.CreateDirectory(".git");
        gitDirectory.CreateFile("config").WriteAllText("");

        var zeroObjectId = new string('0', objectNameFormat.HashSize * 2);
        var baseCommit = Encoding.UTF8.GetBytes(
            $"tree {zeroObjectId}\n" +
            "committer Test <test@example.com> 0 +0000\n\nbase\n");
        var commit = Encoding.UTF8.GetBytes(
            $"tree {zeroObjectId}\n" +
            "author Test <test@example.com> 1 +0000\n" +
            committerLine +
            "\nmessage\n");
        var objectId = GetObjectId("commit", commit, objectNameFormat);
        gitDirectory.CreateFile("HEAD").WriteAllText(objectId + "\n");

        if (packed)
        {
            WriteDeltaPack(gitDirectory.Path, baseCommit, commit, offsetDelta, indexVersion, objectNameFormat);
        }
        else
        {
            WriteLooseObject(gitDirectory.Path, objectId, "commit", commit);
        }

        return (repositoryDirectory, gitDirectory, objectId);
    }

    private static void WriteLooseObject(string gitDirectory, string objectId, string type, byte[] content)
    {
        var directory = Path.Combine(gitDirectory, "objects", objectId[..2]);
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, objectId[2..]));
        WriteCompressed(stream, GetHashedObject(type, content));
    }

    private static void WriteDeltaPack(
        string gitDirectory,
        byte[] baseCommit,
        byte[] commit,
        bool offsetDelta,
        int indexVersion,
        ObjectNameFormat objectNameFormat)
    {
        var packDirectory = Path.Combine(gitDirectory, "objects", "pack");
        Directory.CreateDirectory(packDirectory);
        var baseObjectId = GetObjectIdBytes("commit", baseCommit, objectNameFormat);
        var objectId = GetObjectIdBytes("commit", commit, objectNameFormat);
        var delta = CreateDelta(baseCommit, commit);
        var packPath = Path.Combine(packDirectory, "pack-test.pack");

        long baseOffset;
        long deltaOffset;
        using (var stream = File.Create(packPath))
        {
            WriteBytes(stream, Encoding.ASCII.GetBytes("PACK"));
            WriteUInt32BigEndian(stream, 2);
            WriteUInt32BigEndian(stream, 2);

            baseOffset = stream.Position;
            WritePackObjectHeader(stream, type: 1, baseCommit.Length);
            WriteCompressed(stream, baseCommit);

            deltaOffset = stream.Position;
            WritePackObjectHeader(stream, type: offsetDelta ? 6 : 7, delta.Length);
            if (offsetDelta)
            {
                WriteOffsetDeltaDistance(stream, deltaOffset - baseOffset);
            }
            else
            {
                WriteBytes(stream, baseObjectId);
            }

            WriteCompressed(stream, delta);
            WriteBytes(stream, new byte[objectNameFormat.HashSize]);
        }

        var entries = new[]
        {
            (ObjectId: baseObjectId, Offset: baseOffset),
            (ObjectId: objectId, Offset: deltaOffset),
        }.OrderBy(entry => entry.ObjectId, ByteArrayComparer.Instance).ToArray();

        using var index = File.Create(Path.ChangeExtension(packPath, ".idx"));
        if (indexVersion == 2)
        {
            WriteUInt32BigEndian(index, 0xff744f63);
            WriteUInt32BigEndian(index, 2);
        }

        for (var i = 0; i < 256; i++)
        {
            WriteUInt32BigEndian(index, (uint)entries.Count(entry => entry.ObjectId[0] <= i));
        }

        if (indexVersion == 1)
        {
            foreach (var entry in entries)
            {
                WriteUInt32BigEndian(index, checked((uint)entry.Offset));
                WriteBytes(index, entry.ObjectId);
            }
        }
        else
        {
            foreach (var entry in entries)
            {
                WriteBytes(index, entry.ObjectId);
            }

            WriteBytes(index, new byte[entries.Length * sizeof(uint)]);
            foreach (var entry in entries)
            {
                WriteUInt32BigEndian(index, checked((uint)entry.Offset));
            }
        }

        WriteBytes(index, new byte[objectNameFormat.HashSize * 2]);
    }

    public static string WriteOffsetDeltaChain(string gitDirectory, int depth, long timestamp)
    {
        var zeroObjectId = new string('0', ObjectNameFormat.Sha1.HashSize * 2);
        var contents = Enumerable.Range(0, depth + 1)
            .Select(
                index => Encoding.UTF8.GetBytes(
                    $"tree {zeroObjectId}\n" +
                    $"committer Test <test@example.com> {timestamp} +0000\n\n{index}\n"))
            .ToArray();
        var objectIds = contents
            .Select(content => GetObjectIdBytes("commit", content, ObjectNameFormat.Sha1))
            .ToArray();
        var offsets = new long[contents.Length];
        var packDirectory = Path.Combine(gitDirectory, "objects", "pack");
        Directory.CreateDirectory(packDirectory);
        var packPath = Path.Combine(packDirectory, "pack-test.pack");

        using (var stream = File.Create(packPath))
        {
            WriteBytes(stream, Encoding.ASCII.GetBytes("PACK"));
            WriteUInt32BigEndian(stream, 2);
            WriteUInt32BigEndian(stream, checked((uint)contents.Length));

            offsets[0] = stream.Position;
            WritePackObjectHeader(stream, type: 1, contents[0].Length);
            WriteCompressed(stream, contents[0]);

            for (var i = 1; i < contents.Length; i++)
            {
                var delta = CreateDelta(contents[i - 1], contents[i]);
                offsets[i] = stream.Position;
                WritePackObjectHeader(stream, type: 6, delta.Length);
                WriteOffsetDeltaDistance(stream, offsets[i] - offsets[i - 1]);
                WriteCompressed(stream, delta);
            }

            WriteBytes(stream, new byte[ObjectNameFormat.Sha1.HashSize]);
        }

        var entries = objectIds
            .Select((objectId, index) => (ObjectId: objectId, Offset: offsets[index]))
            .OrderBy(entry => entry.ObjectId, ByteArrayComparer.Instance)
            .ToArray();
        using (var index = File.Create(Path.ChangeExtension(packPath, ".idx")))
        {
            WriteUInt32BigEndian(index, 0xff744f63);
            WriteUInt32BigEndian(index, 2);
            for (var i = 0; i < 256; i++)
            {
                WriteUInt32BigEndian(index, (uint)entries.Count(entry => entry.ObjectId[0] <= i));
            }

            foreach (var entry in entries)
            {
                WriteBytes(index, entry.ObjectId);
            }

            WriteBytes(index, new byte[entries.Length * sizeof(uint)]);
            foreach (var entry in entries)
            {
                WriteUInt32BigEndian(index, checked((uint)entry.Offset));
            }

            WriteBytes(index, new byte[ObjectNameFormat.Sha1.HashSize * 2]);
        }

        var objectId = string.Concat(objectIds[objectIds.Length - 1].Select(value => value.ToString("x2")));
        File.WriteAllText(Path.Combine(gitDirectory, "HEAD"), objectId + Environment.NewLine);
        return objectId;
    }

    public static void WriteCommitGraph(
        string gitDirectory,
        string objectId,
        long timestamp,
        bool split,
        ObjectNameFormat objectNameFormat = ObjectNameFormat.Sha1)
    {
        var objectIdBytes = ParseObjectId(objectId);
        var fanout = CreateFanout(objectIdBytes);
        var commitData = new byte[objectIdBytes.Length + 16];
        WriteUInt32BigEndian(commitData, objectIdBytes.Length, 0x70000000);
        WriteUInt32BigEndian(commitData, objectIdBytes.Length + 4, 0x70000000);
        WriteUInt32BigEndian(commitData, objectIdBytes.Length + 8, (uint)(1 << 2 | (timestamp >> 32)));
        WriteUInt32BigEndian(commitData, objectIdBytes.Length + 12, (uint)timestamp);

        var graphHash = new string('1', objectNameFormat.HashSize * 2);
        string path;
        if (split)
        {
            var directory = Directory.CreateDirectory(Path.Combine(gitDirectory, "objects", "info", "commit-graphs"));
            File.WriteAllText(Path.Combine(directory.FullName, "commit-graph-chain"), graphHash + Environment.NewLine);
            path = Path.Combine(directory.FullName, $"graph-{graphHash}.graph");
        }
        else
        {
            var directory = Directory.CreateDirectory(Path.Combine(gitDirectory, "objects", "info"));
            path = Path.Combine(directory.FullName, "commit-graph");
        }

        WriteChunkFile(
            path,
            Encoding.ASCII.GetBytes("CGPH"),
            version: 1,
            hashVersion: objectNameFormat == ObjectNameFormat.Sha1 ? (byte)1 : (byte)2,
            baseFileCount: 0,
            additionalHeader: Array.Empty<byte>(),
            (OidFanoutChunk, fanout),
            (OidLookupChunk, objectIdBytes),
            (CommitDataChunk, commitData));
    }

    public static void WriteMultiPackIndex(
        string gitDirectory,
        bool incremental,
        uint? encodedOffset = null,
        ObjectNameFormat objectNameFormat = ObjectNameFormat.Sha1)
    {
        var packDirectory = Path.Combine(gitDirectory, "objects", "pack");
        var indexPath = Directory.EnumerateFiles(packDirectory, "pack-*.idx").Single();
        var entries = ReadVersion2PackIndex(indexPath, objectNameFormat.HashSize);
        var fanout = CreateFanout(entries.Select(entry => entry.ObjectId).ToArray());
        var oidLookup = entries.SelectMany(entry => entry.ObjectId).ToArray();
        var offsets = new byte[entries.Length * 2 * sizeof(uint)];
        for (var i = 0; i < entries.Length; i++)
        {
            WriteUInt32BigEndian(offsets, i * 2 * sizeof(uint), 0);
            WriteUInt32BigEndian(
                offsets,
                i * 2 * sizeof(uint) + sizeof(uint),
                encodedOffset ?? checked((uint)entries[i].Offset));
        }

        var hash = new string('2', objectNameFormat.HashSize * 2);
        string path;
        if (incremental)
        {
            var directory = Directory.CreateDirectory(Path.Combine(packDirectory, "multi-pack-index.d"));
            File.WriteAllText(Path.Combine(directory.FullName, "multi-pack-index-chain"), hash + Environment.NewLine);
            path = Path.Combine(directory.FullName, $"multi-pack-index-{hash}.midx");
        }
        else
        {
            path = Path.Combine(packDirectory, "multi-pack-index");
        }

        WriteChunkFile(
            path,
            Encoding.ASCII.GetBytes("MIDX"),
            version: 2,
            hashVersion: objectNameFormat == ObjectNameFormat.Sha1 ? (byte)1 : (byte)2,
            baseFileCount: 0,
            additionalHeader: GetBigEndianBytes(1),
            (PackNamesChunk, PadToUInt32(Encoding.UTF8.GetBytes(Path.GetFileName(indexPath) + "\0"))),
            (OidFanoutChunk, fanout),
            (OidLookupChunk, oidLookup),
            (ObjectOffsetsChunk, offsets));
    }

    public static void MovePackOffsetIntoIndexTrailer(string gitDirectory, string objectId)
    {
        var indexPath = Directory.EnumerateFiles(Path.Combine(gitDirectory, "objects", "pack"), "pack-*.idx").Single();
        var entries = ReadVersion2PackIndex(indexPath, ObjectNameFormat.Sha1.HashSize);
        var objectIdBytes = ParseObjectId(objectId);
        var entryIndex = Array.FindIndex(entries, entry => entry.ObjectId.SequenceEqual(objectIdBytes));
        Assert.True(entryIndex >= 0);

        const int objectIdsPosition = 2 * sizeof(uint) + 256 * sizeof(uint);
        var offsetsPosition =
            objectIdsPosition +
            entries.Length * ObjectNameFormat.Sha1.HashSize +
            entries.Length * sizeof(uint);
        using var stream = File.Open(indexPath, FileMode.Open, FileAccess.Write);
        stream.Position = offsetsPosition + entryIndex * sizeof(uint);
        WriteUInt32BigEndian(stream, 0x80000000);
        stream.Position = offsetsPosition + entries.Length * sizeof(uint);
        WriteUInt64BigEndian(stream, (ulong)entries[entryIndex].Offset);
    }

    public static void PointPackIndexAtOtherObject(string gitDirectory, string objectId)
    {
        var indexPath = Directory.EnumerateFiles(Path.Combine(gitDirectory, "objects", "pack"), "pack-*.idx").Single();
        var entries = ReadVersion2PackIndex(indexPath, ObjectNameFormat.Sha1.HashSize);
        var objectIdBytes = ParseObjectId(objectId);
        var entryIndex = Array.FindIndex(entries, entry => entry.ObjectId.SequenceEqual(objectIdBytes));
        var otherEntry = entries.Single(entry => !entry.ObjectId.SequenceEqual(objectIdBytes));
        Assert.True(entryIndex >= 0);

        const int objectIdsPosition = 2 * sizeof(uint) + 256 * sizeof(uint);
        var offsetsPosition =
            objectIdsPosition +
            entries.Length * ObjectNameFormat.Sha1.HashSize +
            entries.Length * sizeof(uint);
        using var stream = File.Open(indexPath, FileMode.Open, FileAccess.Write);
        stream.Position = offsetsPosition + entryIndex * sizeof(uint);
        WriteUInt32BigEndian(stream, checked((uint)otherEntry.Offset));
    }

    private static void WriteChunkFile(
        string path,
        byte[] signature,
        byte version,
        byte hashVersion,
        byte baseFileCount,
        byte[] additionalHeader,
        params (uint Id, byte[] Data)[] chunks)
    {
        using var stream = new MemoryStream();
        WriteBytes(stream, signature);
        stream.WriteByte(version);
        stream.WriteByte(hashVersion);
        stream.WriteByte((byte)chunks.Length);
        stream.WriteByte(baseFileCount);
        WriteBytes(stream, additionalHeader);

        var offset = stream.Position + (chunks.Length + 1) * 12;
        foreach (var chunk in chunks)
        {
            WriteUInt32BigEndian(stream, chunk.Id);
            WriteUInt64BigEndian(stream, (ulong)offset);
            offset += chunk.Data.Length;
        }

        WriteUInt32BigEndian(stream, 0);
        WriteUInt64BigEndian(stream, (ulong)offset);
        foreach (var chunk in chunks)
        {
            WriteBytes(stream, chunk.Data);
        }

        var data = stream.ToArray();
        using var hash = hashVersion == 1 ? (HashAlgorithm)SHA1.Create() : SHA256.Create();
        var checksum = hash.ComputeHash(data);
        using var file = File.Create(path);
        WriteBytes(file, data);
        WriteBytes(file, checksum);
    }

    private static (byte[] ObjectId, long Offset)[] ReadVersion2PackIndex(string path, int hashSize)
    {
        using var stream = File.OpenRead(path);
        Assert.Equal(0xff744f63u, ReadUInt32BigEndian(stream));
        Assert.Equal(2u, ReadUInt32BigEndian(stream));
        stream.Position = 2 * sizeof(uint) + 255 * sizeof(uint);
        var count = ReadUInt32BigEndian(stream);
        var entries = new (byte[] ObjectId, long Offset)[count];
        var objectIdsPosition = 2 * sizeof(uint) + 256 * sizeof(uint);
        var offsetsPosition = objectIdsPosition + count * hashSize + count * sizeof(uint);
        for (var i = 0; i < count; i++)
        {
            stream.Position = objectIdsPosition + i * hashSize;
            var objectId = new byte[hashSize];
            Assert.Equal(objectId.Length, stream.Read(objectId, 0, objectId.Length));
            stream.Position = offsetsPosition + i * sizeof(uint);
            entries[i] = (objectId, ReadUInt32BigEndian(stream));
        }

        return entries;
    }

    private static byte[] CreateFanout(params byte[][] objectIds)
    {
        var result = new byte[256 * sizeof(uint)];
        for (var i = 0; i < 256; i++)
        {
            WriteUInt32BigEndian(result, i * sizeof(uint), (uint)objectIds.Count(objectId => objectId[0] <= i));
        }

        return result;
    }

    internal static byte[] ParseObjectId(string value)
    {
        var result = new byte[value.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = byte.Parse(value.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
        }

        return result;
    }

    private static byte[] PadToUInt32(byte[] value)
    {
        Array.Resize(ref value, (value.Length + 3) & ~3);
        return value;
    }

    private static byte[] GetBigEndianBytes(uint value)
    {
        var result = new byte[sizeof(uint)];
        WriteUInt32BigEndian(result, 0, value);
        return result;
    }

    private static byte[] CreateDelta(byte[] source, byte[] result)
    {
        using var stream = new MemoryStream();
        WriteVariableLengthInteger(stream, source.Length);
        WriteVariableLengthInteger(stream, result.Length);

        var offset = 0;
        while (offset < source.Length && offset < result.Length && source[offset] == result[offset])
        {
            offset++;
        }

        WriteCopyInstruction(stream, offset);
        while (offset < result.Length)
        {
            var count = Math.Min(127, result.Length - offset);
            stream.WriteByte((byte)count);
            stream.Write(result, offset, count);
            offset += count;
        }

        return stream.ToArray();
    }

    private static void WriteCopyInstruction(Stream stream, int size)
    {
        var command = 0x80;
        var sizeBytes = new byte[3];
        var sizeByteCount = 0;
        for (var i = 0; i < sizeBytes.Length; i++)
        {
            var value = (byte)(size >> (i * 8));
            if (value != 0)
            {
                command |= 1 << (i + 4);
                sizeBytes[sizeByteCount++] = value;
            }
        }

        stream.WriteByte((byte)command);
        stream.Write(sizeBytes, 0, sizeByteCount);
    }

    private static void WritePackObjectHeader(Stream stream, int type, int size)
    {
        var first = (byte)((type << 4) | (size & 0x0f));
        size >>= 4;
        if (size != 0)
        {
            first |= 0x80;
        }

        stream.WriteByte(first);
        while (size != 0)
        {
            var value = (byte)(size & 0x7f);
            size >>= 7;
            if (size != 0)
            {
                value |= 0x80;
            }

            stream.WriteByte(value);
        }
    }

    private static void WriteVariableLengthInteger(Stream stream, int value)
    {
        do
        {
            var current = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            stream.WriteByte(current);
        }
        while (value != 0);
    }

    private static void WriteOffsetDeltaDistance(Stream stream, long value)
    {
        var bytes = new byte[10];
        var position = bytes.Length - 1;
        bytes[position] = (byte)(value & 0x7f);
        while ((value >>= 7) != 0)
        {
            value--;
            bytes[--position] = (byte)(0x80 | (value & 0x7f));
        }

        stream.Write(bytes, position, bytes.Length - position);
    }

    private static string GetObjectId(string type, byte[] content, ObjectNameFormat objectNameFormat)
        => string.Concat(GetObjectIdBytes(type, content, objectNameFormat).Select(value => value.ToString("x2")));

    private static byte[] GetObjectIdBytes(string type, byte[] content, ObjectNameFormat objectNameFormat)
    {
        using var hash = objectNameFormat == ObjectNameFormat.Sha1 ? (HashAlgorithm)SHA1.Create() : SHA256.Create();
        return hash.ComputeHash(GetHashedObject(type, content));
    }

    private static byte[] GetHashedObject(string type, byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"{type} {content.Length}\0");
        var result = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(content, 0, result, header.Length, content.Length);
        return result;
    }

    private static void WriteCompressed(Stream stream, byte[] content)
    {
        stream.WriteByte(0x78);
        stream.WriteByte(0x9c);
        using (var compressor = new DeflateStream(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(content, 0, content.Length);
        }

        WriteUInt32BigEndian(stream, ComputeAdler32(content));
    }

    private static uint ComputeAdler32(byte[] value)
    {
        var a = 1u;
        var b = 0u;
        foreach (var current in value)
        {
            a = (a + current) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static void WriteBytes(Stream stream, byte[] value)
        => stream.Write(value, 0, value.Length);

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteUInt64BigEndian(Stream stream, ulong value)
    {
        WriteUInt32BigEndian(stream, (uint)(value >> 32));
        WriteUInt32BigEndian(stream, (uint)value);
    }

    private static uint ReadUInt32BigEndian(Stream stream)
        => ((uint)stream.ReadByte() << 24) |
           ((uint)stream.ReadByte() << 16) |
           ((uint)stream.ReadByte() << 8) |
           (uint)stream.ReadByte();

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            for (var i = 0; i < left!.Length; i++)
            {
                var result = left[i].CompareTo(right![i]);
                if (result != 0)
                {
                    return result;
                }
            }

            return 0;
        }
    }
}
