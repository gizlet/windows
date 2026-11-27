// The Store end to end, as Gizlet Launcher for Windows does it, against the live Gizlet Store:
// two local profiles, search, install, open the app in profile A with the Launcher's environment,
// let it save, then check that the saves and favourites are profile A's alone and survive an
// uninstall and a reinstall.
//
//   Gizlet.StoreCheck --query prince --id io.gizlet.littleprince [--lang en|zh-CN] [--gl DIR] [--env NAME=VALUE ...]
//
// --gl copies the OpenGL DLLs in DIR (Mesa's llvmpipe on GPU-less CI runners) next to the app's
// entry; --env adds variables for the app, such as a scripted replay.
using System.Diagnostics;
using Gizlet.Core;
using Gizlet.Core.Library;
using Gizlet.Core.Packages;
using Gizlet.Core.Profiles;
using Gizlet.Core.Store;

string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var query = Arg("--query") ?? throw new ArgumentException("--query");
var id = Arg("--id") ?? throw new ArgumentException("--id");
var gl = Arg("--gl");
// GIZLET_LANG, as the Launcher sets it from profile A's language.
var language = Arg("--lang") ?? "en";
if (language is not ("en" or "zh-CN")) throw new ArgumentException("--lang is en or zh-CN");
var env = args.Select((a, i) => (a, i)).Where(p => p.a == "--env" && p.i + 1 < args.Length).Select(p => args[p.i + 1].Split('=', 2)).ToList();

var root = Path.Combine(Path.GetTempPath(), "gizlet-store-check-" + Guid.NewGuid().ToString("N"));
var layout = new DataLayout(root);
var target = WindowsTarget.Current;
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var store = new StoreClient(http, target);
var library = new AppLibrary(layout, target);
var profiles = new ProfileStore(layout);
void Step(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");
void Check(bool ok, string what)
{
    if (!ok) throw new InvalidOperationException("FAILED: " + what);
    Step("ok   " + what);
}

var a = profiles.Current("A");
var b = profiles.Create("B");

// Search -> install.
var found = await store.SearchAsync(query);
var gizlet = found.Gizlets.FirstOrDefault(g => g.Id == id);
Check(gizlet is not null, $"search \"{query}\" finds {id} for {target.Preferred}");
var install = await store.InstallAsync(gizlet!);
var file = await store.DownloadAsync(install.Package!, layout.Downloads);
var app = library.Install(file, new AppOrigin(install.Repo, install.RepositoryId, install.Tag), _ => false);
File.Delete(file);
Check(app.Id == id && app.Origin?.RepositoryId == install.RepositoryId, $"installs {app.Id} {app.Manifest.Version} ({install.Package!.Target}) from {install.Repo}");
if (gl is not null)
{
    foreach (var dll in Directory.GetFiles(gl, "*.dll")) File.Copy(dll, Path.Combine(Path.GetDirectoryName(app.EntryPath)!, Path.GetFileName(dll)), true);
}

// Launch in profile A -> save.
profiles.SaveSettings(a.Id, profiles.Settings(a.Id) with { Favorites = [app.Id] });
var savesA = profiles.SaveDirectory(a.Id, app.Id);
var start = AppLaunch.StartInfo(app, a.Id, savesA, language);
foreach (var pair in env) start.Environment[pair[0]] = pair[1];
start.RedirectStandardOutput = start.RedirectStandardError = true;
using (var process = Process.Start(start)!)
{
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    var exited = process.WaitForExit(TimeSpan.FromMinutes(5));
    if (!exited) process.Kill(entireProcessTree: true);
    Console.WriteLine(await output);
    Console.Error.WriteLine(await error);
    Check(exited && process.ExitCode == 0, $"{app.Id} runs in profile A and exits cleanly (exit {(exited ? process.ExitCode : -1)})");
}
var saved = Directory.GetFiles(savesA, "*", SearchOption.AllDirectories);
Check(saved.Length > 0, $"progress is saved in profile A: {string.Join(", ", saved.Select(Path.GetFileName))}");

// Profile B is apart.
profiles.Switch(b.Id);
Check(!Directory.Exists(layout.Saves(b.Id, app.Id)) || Directory.GetFiles(layout.Saves(b.Id, app.Id), "*", SearchOption.AllDirectories).Length == 0, "profile B has no saves of A");
Check(profiles.Settings(b.Id).Favorites.Count == 0 && profiles.Settings(a.Id).Favorites.Contains(app.Id), "favourites are per profile");

// Uninstall keeps the saves; reinstalling finds them.
library.Uninstall(app.Id, _ => false);
Check(library.Find(app.Id) is null && Directory.GetFiles(savesA, "*", SearchOption.AllDirectories).Length == saved.Length, "uninstalling keeps profile A's saves");
file = await store.DownloadAsync(install.Package!, layout.Downloads);
library.Install(file, new AppOrigin(install.Repo, install.RepositoryId, install.Tag), _ => false);
Check(library.Find(app.Id) is not null && profiles.Settings(a.Id).Favorites.Contains(app.Id), "reinstalling keeps the favourite and the saves");
Step("Store end to end passed");
try
{
    Directory.Delete(root, recursive: true);
}
catch (IOException)
{
}
return 0;
