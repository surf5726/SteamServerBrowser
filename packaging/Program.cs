using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Packager <linux-x64 publish directory> <output directory> <Linux source directory>");
    return 2;
}
string publish = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
string source = Path.GetFullPath(args[2]);
const string release = "SteamServerBrowser-2.71.7-linux-x64";
Directory.CreateDirectory(output);
byte[] elf = File.ReadAllBytes(Path.Combine(publish, "SteamServerBrowser"));
if (elf.Length < 64 || !elf.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }) || elf[4] != 2 || BitConverter.ToUInt16(elf, 18) != 62)
    throw new InvalidDataException("Published launcher is not an x86_64 Linux ELF.");
foreach (string required in new[] { "libcoreclr.so", "libhostfxr.so", "libSkiaSharp.so", "SteamServerBrowser.dll", "SteamServerBrowser.png" })
    if (!File.Exists(Path.Combine(publish, required))) throw new FileNotFoundException("Missing runtime file", required);

var files = Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories)
    .Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
if (files.Any(f => Path.GetFileName(f) == "run.sh")) throw new InvalidDataException("Obsolete run.sh must not be packaged.");
byte[] portable = TarGzip(writer =>
{
    foreach (var path in files)
        AddFile(writer, release + "/" + Path.GetRelativePath(publish, path).Replace('\\', '/'), path, Path.GetFileName(path) == "SteamServerBrowser");
    AddFile(writer, release + "/README.md", Path.Combine(source, "README.md"));
    AddFile(writer, release + "/THIRD-PARTY-NOTICES.md", Path.Combine(source, "THIRD-PARTY-NOTICES.md"));
    foreach (var license in Directory.EnumerateFiles(Path.Combine(source, "licenses")))
        AddFile(writer, release + "/licenses/" + Path.GetFileName(license), license);
});
string tarPath = Path.Combine(output, release + ".tar.gz");
File.WriteAllBytes(tarPath, portable);
VerifyTar(portable, release + "/SteamServerBrowser");
File.Copy(Path.Combine(source, "README.md"), Path.Combine(output, "Linux-README.md"), true);
var manifests = new StringBuilder();
foreach (var path in new[] { tarPath, Path.Combine(output, "Linux-README.md") })
{
    using var input = File.OpenRead(path);
    manifests.Append(Convert.ToHexStringLower(SHA256.HashData(input))).Append("  ").Append(Path.GetFileName(path)).Append('\n');
    Console.WriteLine($"{Path.GetFileName(path)}: {new FileInfo(path).Length:N0} bytes");
}
File.WriteAllText(Path.Combine(output, "SHA256SUMS"), manifests.ToString(), new UTF8Encoding(false));
Console.WriteLine("Portable package validated: x86_64 ELF, runtime libraries, app icon, safe paths and executable permissions.");
return 0;

static byte[] TarGzip(Action<TarWriter> write)
{
    using var memory = new MemoryStream();
    using (var gzip = new GZipStream(memory, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: false)) write(writer);
    return memory.ToArray();
}
static void AddFile(TarWriter writer, string name, string path, bool executable = false)
{
    using var stream = File.OpenRead(path);
    writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
    {
        DataStream = stream, Uid = 0, Gid = 0, UserName = "root", GroupName = "root",
        Mode = (UnixFileMode)Convert.ToInt32(executable ? "755" : "644", 8)
    });
}
static void VerifyTar(byte[] archive, params string[] executables)
{
    using var stream = new MemoryStream(archive);
    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
    using var reader = new TarReader(gzip);
    var missing = executables.ToHashSet(StringComparer.Ordinal);
    while (reader.GetNextEntry() is { } entry)
    {
        if (entry.Name.StartsWith('/') || entry.Name.Split('/').Contains("..")) throw new InvalidDataException("Unsafe tar path");
        if (!missing.Remove(entry.Name)) continue;
        if ((entry.Mode & UnixFileMode.UserExecute) == 0) throw new InvalidDataException("Missing executable permission: " + entry.Name);
    }
    if (missing.Count > 0) throw new InvalidDataException("Missing executable: " + string.Join(", ", missing));
}
