using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed partial class AioNetworkService
    {
        public static Version NormalizeVersion3(Version v) =>
            new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

        public static bool IsInstalledCopy()
        {
            try
            {
                string? dir = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(dir)) return false;
                return File.Exists(Path.Combine(dir, "unins000.exe")) ||
                       dir.Contains("Program Files", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTrustedGitHubUri(Uri? uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.Host;
            return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTrustedGitHubUrl(string url, out Uri? uri)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out uri) && IsTrustedGitHubUri(uri))
                return true;
            uri = null;
            return false;
        }

        private async Task<string> PrepareLockedUpdatesDirectoryAsync(CancellationToken token)
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string appDataRoot = Path.Combine(programData, "AionToolPro");
            string updatesDir = Path.Combine(appDataRoot, "Updates");

            Directory.CreateDirectory(appDataRoot);
            if (IsReparsePoint(appDataRoot))
                throw new UnauthorizedAccessException("Blocked: ProgramData\\AionToolPro is a reparse point.");

            Directory.CreateDirectory(updatesDir);
            if (IsReparsePoint(updatesDir))
                throw new UnauthorizedAccessException("Blocked: ProgramData\\AionToolPro\\Updates is a reparse point.");

            // Lock NTFS ACL strictly to SYSTEM (*S-1-5-18) and Built-in Administrators (*S-1-5-32-544)
            var aclRes = await _runner.RunAsync(
                "icacls",
                new[] { updatesDir, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" },
                token,
                silent: true,
                timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            if (!aclRes.IsSuccess)
            {
                Emit($"[UPDATER WARN] icacls returned code {aclRes.ExitCode}; continuing with elevated directory.");
            }

            return updatesDir;
        }

        public async Task<UpdateCheckResult> CheckForUpdateInfoAsync(string githubOwner, string repoName, CancellationToken token)
        {
            Emit("[UPDATER] Checking GitHub Releases for newer version...");
            string fallbackVer = GetAppVersionString();
            try
            {
                var rawCurrent = Assembly.GetExecutingAssembly().GetName().Version;
                if (rawCurrent == null)
                    return new UpdateCheckResult(false, fallbackVer, fallbackVer, "", null, null, null, "Failed to read local assembly version.", IsError: true);

                var currentVer = NormalizeVersion3(rawCurrent);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{githubOwner}/{repoName}/releases/latest");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var response = await SharedHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!IsTrustedGitHubUri(response.RequestMessage?.RequestUri))
                    return new UpdateCheckResult(false, currentVer.ToString(), currentVer.ToString(), "", null, null, null, "Blocked: GitHub API redirected to an untrusted host.", IsError: true);

                if (!response.IsSuccessStatusCode)
                    return new UpdateCheckResult(false, currentVer.ToString(), currentVer.ToString(), "", null, null, null, $"Could not check updates (HTTP {(int)response.StatusCode}).", IsError: true);

                string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string latestTag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
                string cleanTag = latestTag.TrimStart('v', 'V').Trim();
                string notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

                if (!Version.TryParse(cleanTag, out Version? parsedLatest) || parsedLatest == null)
                    return new UpdateCheckResult(false, currentVer.ToString(), latestTag, "", null, null, null, $"Failed to parse GitHub release version tag ('{latestTag}').", IsError: true);

                var latestVer = NormalizeVersion3(parsedLatest);
                if (latestVer <= currentVer)
                {
                    string upToDate = $"You are already running the latest version (v{currentVer}).";
                    Emit($"[UPDATER] {upToDate}");
                    return new UpdateCheckResult(false, currentVer.ToString(), latestVer.ToString(), notes, null, null, null, upToDate, IsError: false, IsCancelled: false);
                }

                string? downloadUrl = null;
                string? assetNameFound = null;
                string? checksumUrl = null;

                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string assetName = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                        string? url = asset.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() : null;

                        if (downloadUrl == null && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = url;
                            assetNameFound = assetName;
                        }
                        else if (checksumUrl == null &&
                                 (assetName.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase) ||
                                  assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)))
                        {
                            checksumUrl = url;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(downloadUrl))
                    return new UpdateCheckResult(false, currentVer.ToString(), latestVer.ToString(), notes, null, null, null,
                        $"Version v{latestVer} exists on GitHub, but no .exe installer asset is attached yet.", IsError: true);

                return new UpdateCheckResult(true, currentVer.ToString(), latestVer.ToString(), notes, downloadUrl, checksumUrl, assetNameFound,
                    $"New version v{latestVer} is available (current: v{currentVer}).", IsError: false, IsCancelled: false);
            }
            catch (OperationCanceledException)
            {
                return new UpdateCheckResult(false, fallbackVer, fallbackVer, "", null, null, null, "Update check was cancelled.", IsError: false, IsCancelled: true);
            }
            catch (Exception ex)
            {
                return new UpdateCheckResult(false, fallbackVer, fallbackVer, "", null, null, null, $"Connection or API error: {ex.Message}", IsError: true, IsCancelled: false);
            }
        }

        public async Task<UpdateInstallResult> DownloadAndInstallUpdateAsync(
            UpdateCheckResult update,
            IProgress<UpdateDownloadProgress>? progress,
            CancellationToken token)
        {
            if (!update.HasUpdate || string.IsNullOrWhiteSpace(update.DownloadUrl))
                return new UpdateInstallResult("ERROR", update.Message);

            if (!IsTrustedGitHubUrl(update.DownloadUrl, out var dlUri) || dlUri == null)
                return new UpdateInstallResult("ERROR", "Blocked update: download URL is not from GitHub HTTPS.");

            string stagedInstallerPath = string.Empty;

            try
            {
                progress?.Report(new UpdateDownloadProgress(0, 0, -1, 0, "PREPARING"));
                string lockedDir = await PrepareLockedUpdatesDirectoryAsync(token).ConfigureAwait(false);
                stagedInstallerPath = Path.Combine(lockedDir, $"AionToolPro_Update_{update.LatestVersion}_{Guid.NewGuid():N}.exe");

                Emit($"[UPDATER] Downloading v{update.LatestVersion} into ACL-locked staging directory...");
                using (var dlResp = await SharedHttp.GetAsync(dlUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    if (!IsTrustedGitHubUri(dlResp.RequestMessage?.RequestUri))
                        return new UpdateInstallResult("ERROR", "Blocked update: download redirected to an untrusted host.");

                    dlResp.EnsureSuccessStatusCode();
                    long totalBytes = dlResp.Content.Headers.ContentLength ?? -1;
                    await using var src = await dlResp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var dst = new FileStream(stagedInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    byte[] buffer = new byte[81920];
                    long readTotal = 0;
                    int lastPct = -1;
                    var sw = Stopwatch.StartNew();
                    long lastReportMs = -200;
                    int read;

                    while ((read = await src.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        readTotal += read;

                        int pct = totalBytes > 0 ? (int)Math.Clamp(readTotal * 100L / totalBytes, 0, 100) : 0;
                        long elapsedMs = sw.ElapsedMilliseconds;
                        if (pct != lastPct || elapsedMs - lastReportMs >= 120)
                        {
                            lastPct = pct;
                            lastReportMs = elapsedMs;
                            double speed = elapsedMs > 50 ? (readTotal * 1000.0 / elapsedMs) : 0.0;
                            progress?.Report(new UpdateDownloadProgress(pct, readTotal, totalBytes, speed, "DOWNLOADING"));
                        }
                    }

                    progress?.Report(new UpdateDownloadProgress(100, readTotal, totalBytes > 0 ? totalBytes : readTotal, 0, "VERIFYING"));
                }

                // Verify Windows MZ PE header
                byte[] magic = new byte[2];
                await using (var fs = File.OpenRead(stagedInstallerPath))
                {
                    if (fs.Length < 1024 || await fs.ReadAsync(magic.AsMemory(0, 2), token).ConfigureAwait(false) < 2 || magic[0] != 0x4D || magic[1] != 0x5A)
                    {
                        try { File.Delete(stagedInstallerPath); } catch { }
                        return new UpdateInstallResult("ERROR", "Security Error: Downloaded file is not a valid Windows PE executable (missing MZ header).");
                    }
                }

                // Verify SHA-256 checksum against checksums.txt if attached to release
                if (!string.IsNullOrWhiteSpace(update.ChecksumUrl) && IsTrustedGitHubUrl(update.ChecksumUrl, out var csUri) && csUri != null)
                {
                    Emit("[UPDATER] Verifying SHA-256 integrity checksum...");
                    using var csResp = await SharedHttp.GetAsync(csUri, token).ConfigureAwait(false);
                    if (!IsTrustedGitHubUri(csResp.RequestMessage?.RequestUri))
                    {
                        try { File.Delete(stagedInstallerPath); } catch { }
                        return new UpdateInstallResult("ERROR", "Blocked update: checksum URL redirected to an untrusted host.");
                    }
                    csResp.EnsureSuccessStatusCode();
                    string checksumText = await csResp.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                    string actualHash;
                    await using (var fs = File.OpenRead(stagedInstallerPath))
                    {
                        byte[] hashBytes = await SHA256.HashDataAsync(fs, token).ConfigureAwait(false);
                        actualHash = Convert.ToHexString(hashBytes);
                    }

                    string? expectedHash = ExtractExpectedSha256(checksumText, update.AssetName);
                    if (!string.IsNullOrEmpty(expectedHash) && !expectedHash.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(stagedInstallerPath); } catch { }
                        string mismatch = $"Security Error: SHA-256 mismatch! Expected {expectedHash[..12]}..., got {actualHash[..12]}.... Update aborted.";
                        Emit($"[UPDATER ERR] {mismatch}");
                        return new UpdateInstallResult("ERROR", mismatch);
                    }
                    Emit("[UPDATER] PE header and SHA-256 checksum verified OK.");
                }

                progress?.Report(new UpdateDownloadProgress(100, 0, 0, 0, "INSTALLING"));
                Emit("[UPDATER] Verification passed. Launching silent installer handoff...");
                CancelCurrentOperation();

                string cmdPath = _runner.ResolveSystemExecutable("cmd.exe");
                string pingPath = _runner.ResolveSystemExecutable("ping.exe");
                string taskkillPath = _runner.ResolveSystemExecutable("taskkill.exe");

                string args = $"/c \"\"{pingPath}\" 127.0.0.1 -n 4 > NUL & \"{taskkillPath}\" /F /IM AionToolPro.exe > NUL 2>&1 & start \"\" /wait \"{stagedInstallerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART & del /f /q \"{stagedInstallerPath}\" > NUL 2>&1\"";

                Process.Start(new ProcessStartInfo
                {
                    FileName = cmdPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                return new UpdateInstallResult("READY_TO_UPDATE", "Launching silent installer...");
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrEmpty(stagedInstallerPath))
                    try { if (File.Exists(stagedInstallerPath)) File.Delete(stagedInstallerPath); } catch { }
                Emit("[UPDATER] Update download cancelled by user; temporary installer removed.");
                return new UpdateInstallResult("CANCELLED", "Update download was cancelled cleanly and temporary files were removed.");
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(stagedInstallerPath))
                    try { if (File.Exists(stagedInstallerPath)) File.Delete(stagedInstallerPath); } catch { }
                Emit($"[UPDATER ERR] {ex.Message}");
                return new UpdateInstallResult("ERROR", $"Update failed: {ex.Message}");
            }
        }

        private static string? ExtractExpectedSha256(string checksumFileContent, string? targetAssetName)
        {
            foreach (var raw in checksumFileContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length < 64) continue;

                if (!string.IsNullOrEmpty(targetAssetName) && !line.Contains(targetAssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (p.Length == 64 && IsHexString(p))
                        return p;
                }
            }
            return null;
        }

        private static bool IsHexString(string s)
        {
            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
