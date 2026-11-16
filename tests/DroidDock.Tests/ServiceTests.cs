using System.IO.Compression;
using DroidDock.Models;
using DroidDock.Services;

namespace DroidDock.Tests;

public class AdbParsingTests
{
    [Fact]
    public void ParseDevices_reads_serials_and_states()
    {
        var output = "List of devices attached\r\nemulator-5554\tdevice\r\nemulator-5556\toffline\r\nR58M12345\tunauthorized\r\n\r\n";

        var devices = AdbService.ParseDevices(output);

        Assert.Equal(3, devices.Count);
        Assert.True(devices[0].IsEmulator && devices[0].IsOnline);
        Assert.False(devices[1].IsOnline);
        Assert.False(devices[2].IsEmulator);
    }

    [Fact]
    public void ParseDevices_ignores_daemon_noise()
    {
        var output = "* daemon not running; starting now at tcp:5037\n* daemon started successfully\nList of devices attached\n";
        Assert.Empty(AdbService.ParseDevices(output));
    }

    [Fact]
    public void ParsePackages_strips_prefix_and_sorts()
    {
        var packages = AdbService.ParsePackages("package:org.zebra\r\npackage:com.apple\r\nnoise\r\n");
        Assert.Equal(["com.apple", "org.zebra"], packages);
    }
}

public class AvdServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "droiddock-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private (string Sdk, string AvdHome, SystemImage Image) Setup()
    {
        var sdk = Path.Combine(_root, "sdk");
        var imageDir = Path.Combine(sdk, "system-images", "android-36", "google_apis_playstore", "x86_64");
        Directory.CreateDirectory(imageDir);
        File.WriteAllText(Path.Combine(imageDir, "system.img"), "");
        File.WriteAllText(Path.Combine(imageDir, "source.properties"),
            "AndroidVersion.ApiLevel=36\nSystemImage.Abi=x86_64\nSystemImage.TagId=google_apis_playstore\nSystemImage.TagDisplay=Google Play\n");
        var image = new SdkPaths(sdk).InstalledImages().Single();
        return (sdk, Path.Combine(_root, "avd"), image);
    }

    [Fact]
    public void InstalledImages_reads_source_properties()
    {
        var (_, _, image) = Setup();

        Assert.Equal("android-36", image.Target);
        Assert.Equal("36", image.ApiLevel);
        Assert.Equal("google_apis_playstore", image.TagId);
        Assert.True(image.HasPlayStore);
        Assert.Contains("Android 16", image.DisplayName);
    }

    [Fact]
    public void Create_then_list_then_delete_round_trips()
    {
        var (sdk, avdHome, image) = Setup();
        Directory.CreateDirectory(avdHome);
        var spec = new AvdSpec("My_Phone", image, DeviceProfile.All[0], 4096, 4, 16);

        var created = AvdService.Create(spec, sdk, avdHome);

        Assert.Equal("My_Phone", created.Name);
        Assert.Equal("My Phone", created.DisplayName);
        Assert.Equal("android-36", created.Target);

        var config = IniFile.Read(Path.Combine(created.Dir, "config.ini"));
        Assert.Equal(@"system-images\android-36\google_apis_playstore\x86_64\", config["image.sysdir.1"]);
        Assert.Equal("4096", config["hw.ramSize"]);
        Assert.Equal("true", config["PlayStore.enabled"]);
        Assert.Equal("16G", config["disk.dataPartition.size"]);

        Assert.Throws<InvalidOperationException>(() => AvdService.Create(spec, sdk, avdHome));

        AvdService.Delete(created);
        Assert.Empty(AvdService.List(avdHome));
    }

    [Theory]
    [InlineData("Pixel_9", true)]
    [InlineData("a.b-c", true)]
    [InlineData("has space", false)]
    [InlineData("..\\evil", false)]
    [InlineData("", false)]
    public void IsValidName_rejects_unsafe_names(string name, bool expected) =>
        Assert.Equal(expected, AvdService.IsValidName(name));
}

public class SdkRepositoryTests
{
    private const string Feed = """
        <sdk:sdk-repository xmlns:sdk="http://schemas.android.com/sdk/android/repo/repository2/03" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <license id="android-sdk-license" type="text">Terms and Conditions</license>
          <channel id="channel-0">stable</channel>
          <channel id="channel-1">beta</channel>
          <remotePackage path="emulator">
            <revision><major>37</major><minor>3</minor><micro>9</micro></revision>
            <display-name>Android Emulator</display-name>
            <uses-license ref="android-sdk-license"/>
            <channelRef ref="channel-1"/>
            <archives><archive><complete><size>10</size><checksum type="sha1">aa</checksum><url>emulator-windows_x64-beta.zip</url></complete><host-os>windows</host-os><host-arch>x64</host-arch></archive></archives>
          </remotePackage>
          <remotePackage path="emulator">
            <revision><major>37</major><minor>2</minor><micro>13</micro></revision>
            <display-name>Android Emulator</display-name>
            <uses-license ref="android-sdk-license"/>
            <channelRef ref="channel-0"/>
            <archives>
              <archive><complete><size>1</size><checksum type="sha1">LINUX</checksum><url>emulator-linux_x64-1.zip</url></complete><host-os>linux</host-os><host-arch>x64</host-arch></archive>
              <archive><complete><size>456614209</size><checksum type="sha1">ABCDEF</checksum><url>emulator-windows_x64-1.zip</url></complete><host-os>windows</host-os><host-arch>x64</host-arch></archive>
            </archives>
          </remotePackage>
          <remotePackage path="system-images;android-36;google_apis_playstore;x86_64">
            <type-details xsi:type="sys-img:sysImgDetailsType">
              <api-level>36</api-level>
              <tag><id>google_apis_playstore</id><display>Google Play</display></tag>
              <abi>x86_64</abi>
            </type-details>
            <revision><major>7</major></revision>
            <display-name>Google Play Intel x86_64 Atom System Image</display-name>
            <uses-license ref="android-sdk-license"/>
            <channelRef ref="channel-0"/>
            <archives><archive><complete><size>1717986918</size><checksum type="sha1">0123</checksum><url>x86_64-36_r07.zip</url></complete></archive></archives>
          </remotePackage>
        </sdk:sdk-repository>
        """;

    private static SdkRepository Load()
    {
        var repo = new SdkRepository();
        repo.Add(Feed, new Uri("https://dl.google.com/android/repository/sys-img/google_apis_playstore/sys-img2-3.xml"));
        return repo;
    }

    [Fact]
    public void Add_keeps_only_stable_windows_archives()
    {
        var emulator = Load().Latest("emulator");

        Assert.NotNull(emulator);
        Assert.Equal(new Version(37, 2, 13), emulator.Revision);
        Assert.Equal("abcdef", emulator.Checksum);
        Assert.EndsWith("/sys-img/google_apis_playstore/emulator-windows_x64-1.zip", emulator.Url.AbsoluteUri);
    }

    [Fact]
    public void Add_reads_licenses_and_system_image_details()
    {
        var repo = Load();
        var image = repo.Packages.Single(p => p.IsSystemImage);

        Assert.Equal("Terms and Conditions", repo.Licenses["android-sdk-license"]);
        Assert.Equal("36", image.ApiLevel);
        Assert.Equal("google_apis_playstore", image.TagId);
        Assert.Equal("1.6 GB", image.SizeText);
    }

    [Fact]
    public void InstallDir_maps_package_path_to_folders() =>
        Assert.Equal(@"C:\sdk\system-images\android-36\google_apis\x86_64",
            SdkRepository.InstallDir(@"C:\sdk", "system-images;android-36;google_apis;x86_64"));

    [Fact]
    public void Extract_strips_top_folder_and_blocks_path_traversal()
    {
        var temp = Path.Combine(Path.GetTempPath(), "droiddock-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var good = Path.Combine(temp, "good.zip");
            using (var zip = ZipFile.Open(good, ZipArchiveMode.Create))
            {
                Write(zip, "emulator/emulator.exe", "x");
                Write(zip, "emulator/lib/a.dll", "y");
            }
            var dest = Path.Combine(temp, "out");
            SdkRepository.ExtractStrippingTopFolder(good, dest);
            Assert.True(File.Exists(Path.Combine(dest, "emulator.exe")));
            Assert.True(File.Exists(Path.Combine(dest, "lib", "a.dll")));

            var evil = Path.Combine(temp, "evil.zip");
            using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
            {
                Write(zip, "pkg/ok.txt", "x");
                Write(zip, "pkg/../../escaped.txt", "y");
            }
            Assert.Throws<InvalidDataException>(() =>
                SdkRepository.ExtractStrippingTopFolder(evil, Path.Combine(temp, "out2")));
            Assert.False(File.Exists(Path.Combine(temp, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
