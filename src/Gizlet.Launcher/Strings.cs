using System.Globalization;
using Gizlet.Core;

namespace Gizlet.Launcher;

/// <summary>
/// Every string the Launcher shows, in English and Simplified Chinese. The language follows the
/// current profile and changes at once: pages read these when they are built again.
/// </summary>
static class S
{
    public static bool Zh { get; private set; }

    /// <summary><c>en</c> or <c>zh-CN</c>, as apps get it in <c>GIZLET_LANG</c>.</summary>
    public static string Language => Zh ? "zh-CN" : "en";

    public static void Use(string choice) =>
        Zh = choice switch
        {
            "zh-CN" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh",
        };

    static string T(string en, string zh) => Zh ? zh : en;

    public static string AppName => T("Gizlet Launcher", "Gizlet 启动器");
    public static string Apps => T("Apps", "应用");
    public static string Store => T("Store", "商店");
    public static string Flash => T("Flash a card", "刷写 TF 卡");
    public static string Settings => T("Settings", "设置");
    public static string Cancel => T("Cancel", "取消");
    public static string Close => T("Close", "关闭");
    public static string Retry => T("Try again", "重试");
    public static string Refresh => T("Refresh", "刷新");
    public static string Delete => T("Delete", "删除");
    public static string Rename => T("Rename", "重命名");
    public static string Save => T("Save", "保存");
    public static string Loading => T("Loading…", "加载中…");

    // Apps
    public static string Favorites => T("Favorites", "收藏");
    public static string AllApps => T("All apps", "全部应用");
    public static string ImportPackage => T("Import .gzl", "导入 .gzl");
    public static string CheckAppUpdates => T("Check for updates", "检查更新");
    public static string Open => T("Open", "打开");
    public static string Running => T("Running", "运行中");
    public static string AddFavorite => T("Add to favorites", "加入收藏");
    public static string RemoveFavorite => T("Remove from favorites", "取消收藏");
    public static string Update => T("Update", "更新");
    public static string Uninstall => T("Uninstall", "卸载");
    public static string UpdateAvailable => T("Update available", "有更新");
    public static string NoApps => T("No apps yet. Find some in the Store, or import a .gzl package.", "还没有应用。去商店看看，或导入一个 .gzl 安装包。");
    public static string GoToStore => T("Open the Store", "打开商店");
    public static string Installing => T("Installing…", "正在安装…");
    public static string Downloading => T("Downloading…", "正在下载…");
    public static string Installed(string name) => T($"{name} is installed.", $"已安装 {name}。");
    public static string UninstallTitle(string name) => T($"Uninstall {name}?", $"卸载 {name}？");
    public static string UninstallBody => T("The app is removed for every profile. Each profile keeps its saves, so installing it again carries on from there.",
        "应用会从所有 Profile 中移除。每个 Profile 的存档都会保留，重新安装后可以接着玩。");
    public static string AppsUpToDate => T("All apps are up to date.", "所有应用都是最新版本。");
    public static string UpdatesFound(int count) => T(count == 1 ? "1 app can be updated." : $"{count} apps can be updated.", $"{count} 个应用可以更新。");
    public static string LocalPackage => T("Installed from a file", "从文件安装");
    public static string Version(string version) => T($"Version {version}", $"版本 {version}");
    public static string PackageFilter => T("Gizlet package", "Gizlet 安装包");

    // Store
    public static string SearchStore => T("Search the Store", "搜索商店");
    public static string Featured => T("Featured", "精选");
    public static string Results(string query) => T($"Results for “{query}”", $"“{query}”的搜索结果");
    public static string NothingFound => T("Nothing found for Windows.", "没有找到能在 Windows 上运行的应用。");
    public static string NothingForWindows => T("No apps for this PC yet.", "暂时还没有适合这台电脑的应用。");
    public static string StoreOffline => T("The Store cannot be reached. Check the internet connection.", "无法连接商店，请检查网络。");
    public static string Get => T("Install", "安装");
    public static string InstalledBadge => T("Installed", "已安装");
    public static string Back => T("Back", "返回");
    public static string Screenshots => T("Screenshots", "截图");
    public static string Controls => T("Controls", "操作");
    public static string Details => T("Details", "详情");
    public static string License => T("License", "许可证");
    public static string Developer => T("Developer", "开发者");
    public static string ViewOnWeb => T("View on gizlet.io", "在 gizlet.io 查看");
    public static string LoadMore => T("Load more", "加载更多");
    public static string Game => T("Game", "游戏");
    public static string App => T("App", "应用");

    // Settings
    public static string Profiles => T("Profiles", "Profiles");
    public static string ProfilesHelp => T("Everyone on this PC can have a profile. Apps are shared; saves, favorites, language and appearance belong to each profile.",
        "这台电脑上的每个人都可以有自己的 Profile。应用是共享的，存档、收藏、语言和外观各自独立。");
    public static string CurrentProfile => T("Current", "当前");
    public static string SwitchTo => T("Switch", "切换");
    public static string NewProfile => T("New profile", "新建 Profile");
    public static string ProfileName => T("Name", "名称");
    public static string DefaultProfileName => T("Default", "默认");
    public static string DeleteProfileTitle(string name) => T($"Delete {name}?", $"删除 {name}？");
    public static string DeleteProfileBody => T("All of this profile's saves, favorites and settings are deleted. This cannot be undone.", "这个 Profile 的所有存档、收藏和设置都会被删除，无法恢复。");
    public static string CloseAppsFirst => T("Close the running apps before switching profiles.", "切换 Profile 前请先关闭正在运行的应用。");
    public static string Language_ => T("Language", "语言");
    public static string FollowWindows => T("Use Windows' language", "跟随 Windows");
    public static string Appearance => T("Appearance", "外观");
    public static string ThemeSystem => T("Use Windows' setting", "跟随 Windows");
    public static string ThemeLight => T("Light", "浅色");
    public static string ThemeDark => T("Dark", "深色");
    public static string LauncherUpdates => T("Launcher updates", "启动器更新");
    public static string CurrentVersion(string version) => T($"Gizlet Launcher {version}", $"Gizlet 启动器 {version}");
    public static string CheckNow => T("Check now", "立即检查");
    public static string Checking => T("Checking…", "正在检查…");
    public static string UpToDate => T("You have the latest version.", "已是最新版本。");
    public static string LastChecked(DateTimeOffset when) => T($"Last checked {when.LocalDateTime:t}", $"上次检查 {when.LocalDateTime:t}");
    public static string UpdateCheckFailed => T("Could not check for updates. The Launcher keeps working; it will try again shortly.", "检查更新失败。启动器可以照常使用，稍后会再试。");
    public static string StartWithWindows => T("Open Gizlet Launcher when I sign in to Windows", "登录 Windows 后打开 Gizlet 启动器");
    public static string DataFolder => T("Data folder", "数据文件夹");
    public static string OpenFolder => T("Open folder", "打开文件夹");

    // Launcher update
    public static string NewLauncher(string version) => T($"Gizlet Launcher {version} is available", $"Gizlet 启动器 {version} 已发布");
    public static string InstallUpdate => T("Update and restart", "更新并重启");
    public static string Later => T("Later", "稍后");
    public static string UpdateDownloading => T("Downloading the update…", "正在下载更新…");
    public static string UpdateVerifying => T("Checking the update's signature…", "正在验证更新签名…");
    public static string UpdateWaitsForFlash => T("The update installs when the card is written.", "写卡完成后再安装更新。");
    public static string UpdateFailed(string why) => T($"The update could not be installed: {why}. This version keeps working.", $"更新未能安装：{why}。当前版本仍可正常使用。");
    public static string UpdateUnsigned => T("the installer is not signed by Gizlet", "安装程序没有 Gizlet 的签名");
    public static string UpdateDevBuild => T("this is a development build", "这是开发版本");

    // Flash
    public static string FlashIntro => T("Write Gizlet OS to a TF card for the R36S. The whole card is erased.", "为 R36S 的 TF 卡写入 Gizlet OS。整张卡会被清空。");
    public static string ChooseCard => T("1. Card", "1. 选择 TF 卡");
    public static string NoCards => T("Insert the TF card in a card reader. Only USB and SD card readers are listed; this PC's own disks never are.",
        "请把 TF 卡插入读卡器。这里只列出 USB 和 SD 读卡器，电脑自身的磁盘不会出现。");
    public static string Disk(int number) => T($"Disk {number}", $"磁盘 {number}");
    public static string NoVolumeName => T("no volume name", "无卷标");
    public static string LargeDisk => T("Over 256 GB: probably not a TF card", "超过 256 GB，可能不是 TF 卡");
    public static string StockCard => T("Looks like the stock card: its games will be lost", "像是原厂卡，上面的游戏会全部丢失");
    public static string HasGizlet => T("Has Gizlet OS", "已装有 Gizlet OS");
    public static string ChooseImage => T("2. Image", "2. 选择镜像");
    public static string OfficialImage => T("Latest Gizlet OS", "最新版 Gizlet OS");
    public static string OfficialImageVersion(string tag) => T($"Latest Gizlet OS ({tag})", $"最新版 Gizlet OS（{tag}）");
    public static string LocalImage => T("An image file on this PC", "这台电脑上的镜像文件");
    public static string Browse => T("Browse…", "浏览…");
    public static string NoFileChosen => T("No file chosen", "未选择文件");
    public static string ImageFilter => T("Card image", "TF 卡镜像");
    public static string LocalImagePanel => T("A local image keeps the display settings it was built with.", "本地镜像使用其自带的屏幕配置。");
    public static string ChoosePanel => T("3. Screen", "3. 选择屏幕");
    public static string Panel4 => T("Panel 4 (NV3051D): V30 boards and most current units", "Panel 4（NV3051D）：V30 主板和目前大多数机器");
    public static string Panel0 => T("Panel 0 (ST7703): earlier units", "Panel 0（ST7703）：较早的机器");
    public static string PanelFromStock => T("Found on the stock card.", "已从原厂卡识别。");
    public static string PanelFromGizlet => T("This card was last written for this screen. Change it if the screen stayed black.", "这张卡上次写入的是这种屏幕。如果之前黑屏，请换一个。");
    public static string PanelUnknown => T("The card does not say which screen the handheld has. Choose one; if the screen stays black after boot, write the card again with the other.",
        "无法从卡上识别屏幕型号，请选择一个。如果开机后黑屏，换另一个重新写卡。");
    public static string WriteCard => T("Write card", "开始写卡");
    public static string EraseTitle => T("Erase this card?", "清空这张卡？");
    public static string EraseBody => T("Everything on this card will be erased and cannot be recovered.", "卡上的所有数据都会被清除，无法恢复。");
    public static string TypeToConfirm(string name) => T($"To confirm, type {name}", $"请输入“{name}”以确认");
    public static string Erase => T("Erase and write", "清空并写入");
    public static string CardChanged => T("The card changed since it was chosen. Check it and confirm again.", "选择之后卡发生了变化，请检查后重新确认。");
    public static string PreparingImage => T("Preparing the image…", "正在准备镜像…");
    public static string AskingPermission => T("Windows asks for permission to write the card…", "Windows 正在请求写卡权限…");
    public static string PermissionDenied => T("Permission was not given. Nothing was written.", "没有获得权限，未写入任何内容。");
    public static string StageChecking => T("Checking the card and image…", "正在检查卡和镜像…");
    public static string StageWriting => T("Writing…", "正在写入…");
    public static string StageVerifying => T("Reading back to verify…", "正在读回校验…");
    public static string StageConfiguring => T("Setting up the screen…", "正在配置屏幕…");
    public static string FlashDone => T("Done. Eject the card, put it in the R36S and switch it on.", "完成。弹出卡，插进 R36S 开机。");
    public static string FlashFailed(string why) => T($"The card was not written: {why}", $"写卡失败：{why}");
    public static string NothingWritten => T("Nothing was written.", "未写入任何内容。");
    public static string HelperUntrusted => T("the card writer is not signed by Gizlet", "写卡程序没有 Gizlet 的签名");

    public static string Size(long bytes) =>
        bytes >= 1_000_000_000 ? string.Create(CultureInfo.CurrentCulture, $"{bytes / 1e9:0.#} GB") : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1e6:0} MB");

    public static string Error(Exception e) => e switch
    {
        GizletException g => Error(g.Error),
        HttpRequestException or TaskCanceledException => T("the network did not answer", "网络没有响应"),
        _ => e.Message,
    };

    public static string Error(GizletError error) => error switch
    {
        GizletError.PackageUnreadable => T("the package is not a valid .gzl file", "安装包不是有效的 .gzl 文件"),
        GizletError.PackageTooLarge => T("the package is too large", "安装包太大"),
        GizletError.PackageBadPath or GizletError.PackageLink or GizletError.PackageDuplicatePath or GizletError.PackageEncrypted =>
            T("the package contains files that cannot be installed safely", "安装包里有无法安全安装的文件"),
        GizletError.PackageNoManifest or GizletError.PackageBadManifest => T("the package's manifest.json is missing or invalid", "安装包的 manifest.json 缺失或无效"),
        GizletError.PackageChecksums => T("the package's files do not match its CHECKSUMS", "安装包内的文件与 CHECKSUMS 不一致"),
        GizletError.PackageService => T("background services do not run on Windows", "后台服务不能在 Windows 上运行"),
        GizletError.PackageWrongPlatform => T("the package is not for Windows on this PC's processor", "这个安装包不适用于这台电脑的 Windows 和处理器"),
        GizletError.PackageNoEntry or GizletError.PackageEntryNotExecutable => T("the package's program is not a Windows program for this PC", "安装包里的程序不是适用于这台电脑的 Windows 程序"),
        GizletError.PackageDownloadMismatch => T("the download was damaged; try again", "下载的文件已损坏，请重试"),
        GizletError.AppRunning => T("the app is running; close it first", "应用正在运行，请先关闭"),
        GizletError.AppNotInstalled => T("the app is not installed", "应用未安装"),
        GizletError.AppOtherRepository => T("an app with the same ID from another source is installed", "已安装了来自其他来源、ID 相同的应用"),
        GizletError.ProfileNotFound => T("the profile no longer exists", "这个 Profile 已不存在"),
        GizletError.ProfileCurrent => T("the current profile cannot be deleted", "不能删除当前 Profile"),
        GizletError.ProfileLast => T("the last profile cannot be deleted", "不能删除最后一个 Profile"),
        GizletError.ProfileBadName => T("choose another name (1 to 40 characters, not used by another profile)", "请换一个名称（1 到 40 个字符，不能与其他 Profile 重复）"),
        GizletError.StoreUnavailable => T("the Store cannot be reached", "无法连接商店"),
        GizletError.StoreNoPackage => T("there is no package for this PC", "没有适合这台电脑的安装包"),
        GizletError.StoreNotReady => T("the release is still being checked; try again in a minute", "这个版本还在检查中，请一分钟后再试"),
        GizletError.SignatureInvalid => T("the signature is not valid", "签名无效"),
        GizletError.UpdateBadManifest => T("the update information is invalid", "更新信息无效"),
        GizletError.UpdateDownloadMismatch => T("the downloaded installer does not match", "下载的安装程序校验不一致"),
        GizletError.UpdatePublisher => T("the installer is not signed by Gizlet", "安装程序没有 Gizlet 的签名"),
        GizletError.ImageBadChecksum => T("the image does not match its checksum", "镜像校验不一致"),
        GizletError.ImageBadLayout => T("the file is not an R36S card image", "这个文件不是 R36S 的镜像"),
        GizletError.ImageTooLarge => T("the image is larger than the card", "镜像比卡还大"),
        GizletError.DiskChanged => T("the card changed or was taken out; nothing was written", "卡被更换或拔出，未写入任何内容"),
        GizletError.DiskNotEligible => T("this disk may not be written", "不允许写入这个磁盘"),
        GizletError.DiskWriteFailed => T("writing failed; the card may need to be written again", "写入失败，可能需要重新写卡"),
        GizletError.DiskVerifyFailed => T("the card did not read back as written; try another card or reader", "读回校验不一致，请换一张卡或读卡器"),
        _ => error.ToString(),
    };
}
