using CodeIndex.PackageNormalize;
using System.IO.Compression;
using System.Text;

namespace CodeIndex.Tests;

public partial class ReleaseWorkflowTests
{
    private static (int ExitCode, string Stdout, string Stderr) RunPackageNormalizeCli(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = PackageNormalizeCli.Run(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static void AssertNoNormalizeTempFiles(string projectRoot, string packagePath)
    {
        Assert.False(File.Exists(packagePath + ".normalize-tmp"));
        Assert.Empty(Directory.GetFiles(projectRoot, ".cdidx-normalize-*.tmp"));
    }

    private static void CreateMinimalNuGetPackage(string packagePath, string corePropertiesFileName)
    {
        var corePropertiesPath = $"package/services/metadata/core-properties/{corePropertiesFileName}";
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);

        WriteZipEntry(archive, "[Content_Types].xml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
              <Override PartName="/{corePropertiesPath}" ContentType="application/vnd.openxmlformats-package.core-properties+xml" />
            </Types>
            """);
        WriteZipEntry(archive, "_rels/.rels", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="R1" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/{corePropertiesPath}" />
            </Relationships>
            """);
        WriteZipEntry(archive, "cdidx.nuspec", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata><id>cdidx</id><version>1.0.0</version></metadata>
            </package>
            """);
        WriteZipEntry(archive, corePropertiesPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <coreProperties xmlns="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" />
            """);
    }

    private static void CreatePackageWithEntries(string packagePath, params (string EntryName, string Content)[] entries)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var entry in entries)
            WriteZipEntry(archive, entry.EntryName, entry.Content);
    }

    private static void CreatePackageWithAttributedEntries(string packagePath, params (string EntryName, string Content, int ExternalAttributes)[] entries)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var entry in entries)
            WriteZipEntry(archive, entry.EntryName, entry.Content, entry.ExternalAttributes);
    }

    private static void WriteZipEntry(ZipArchive archive, string entryName, string content, int? externalAttributes = null)
    {
        var entry = archive.CreateEntry(entryName);
        if (externalAttributes.HasValue)
            entry.ExternalAttributes = externalAttributes.Value;

        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static int UnixRegularFileAttributes(int permissions)
    {
        return unchecked((int)((0x8000u | (uint)permissions) << 16));
    }

    private static int UnixSymlinkAttributes()
    {
        return unchecked((int)((0xA000u | 511u) << 16));
    }

    private static string ReadZipEntryText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Missing ZIP entry: {entryName}");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
