using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AionToolPro.Desktop.Models;

namespace AionToolPro.Desktop.Services
{
    public sealed partial class AioNetworkService
    {
        private static readonly HttpClient UpdateHttp = CreateUpdaterHttpClient();

        private static HttpClient CreateUpdaterHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }
            };
            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"AionToolPro/{GetAppVersionString()}");
            return client;
        }

        public static Version NormalizeVersion3(Version v) =>
            new(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build));

        private static bool TryParseReleaseTagVersion(string? rawTag, out Version? parsed)
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(rawTag)) return false;

            string clean = rawTag.Trim().TrimStart('v', 'V');
            int dashIdx = clean.IndexOfAny(new[] { '-', '+' });
            if (dashIdx > 0) clean = clean[..dashIdx];

            if (Version.TryParse(clean, out var v) && v != null)
            {
                parsed = NormalizeVersion3(v);
                return true;
            }

            var m = Regex.Match(clean, @"(\d+)\.(\d+)(?:\.(\d+))?");
            if (m.Success)
            {
                int maj = int.Parse(m.Groups[1].Value);
                int min = int.Parse(m.Groups[2].Value);
                int pat = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                parsed = new Version(maj, min, pat);
                return true;
            }

            return false;
        }

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
                || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
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

            var aclRes = await _runner.RunAsync(
                "icacls",
                new[] { updatesDir, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" },
                token,
                silent: true,
                timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            if (!aclRes.IsSuccess)
                WriteDiagnosticLog("Updater", $"icacls returned code {aclRes.ExitCode}");

            return updatesDir;
        }

        private async Task<UpdateCheckResult?> CheckForUpdateViaWebFallbackAsync(
            string githubOwner,
            string repoName,
            Version currentVer,
            CancellationToken token)
        {
            try
            {
                string webUrl = $"https://github.com/{githubOwner}/{repoName}/releases/latest";
                WriteDiagnosticLog("Updater", $"Attempting web fallback check: {webUrl}");

                using var req = new HttpRequestMessage(HttpMethod.Get, webUrl);
                using var resp = await UpdateHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var finalUri = resp.RequestMessage?.RequestUri;
                if (!resp.IsSuccessStatusCode || !IsTrustedGitHubUri(finalUri) || finalUri == null)
                    return null;

                var m = Regex.Match(finalUri.AbsolutePath, @"/releases/tag/([^/?#]+)", RegexOptions.IgnoreCase);
                if (!m.Success) return null;

                string tag = Uri.UnescapeDataString(m.Groups[1].Value);
                if (!TryParseReleaseTagVersion(tag, out var latestVer) || latestVer == null)
                    return null;

                if (latestVer <= currentVer)
                {
                    string upToDate = $"You are already running the latest version (v{currentVer}).";
                    Emit($"[UPDATER] {upToDate}");
                    return new UpdateCheckResult(false, currentVer.ToString(), latestVer.ToString(), "", null, null, null, upToDate, IsError: false, IsCancelled: false);
                }

                string expectedAsset = $"AionToolPro_Setup_v{latestVer}.exe";
                string dlUrl = $"https://github.com/{githubOwner}/{repoName}/releases/download/{tag}/{expectedAsset}";
                string csUrl = $"https://github.com/{githubOwner}/{repoName}/releases/download/{tag}/checksums.txt";

                using var headReq = new HttpRequestMessage(HttpMethod.Head, dlUrl);
                using var headResp = await UpdateHttp.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!headResp.IsSuccessStatusCode || !IsTrustedGitHubUri(headResp.RequestMessage?.RequestUri))
                {
                    return new UpdateCheckResult(false, currentVer.ToString(), latestVer.ToString(), "", null, null, null,
                        $"Version v{latestVer} exists on GitHub, but installer asset '{expectedAsset}' is not ready yet.", IsError: true);
                }

                string? validCsUrl = null;
                try
                {
                    using var csHead = new HttpRequestMessage(HttpMethod.Head, csUrl);
                    using var csResp = await UpdateHttp.SendAsync(csHead, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (csResp.IsSuccessStatusCode && IsTrustedGitHubUri(csResp.RequestMessage?.RequestUri))
                        validCsUrl = csUrl;
                }
                catch { }

                WriteDiagnosticLog("Updater", $"Web fallback succeeded: found v{latestVer} ({expectedAsset}, sha256={(validCsUrl != null)})");
                return new UpdateCheckResult(true, currentVer.ToString(), latestVer.ToString(),
                    $"Official release {tag} on GitHub (retrieved via direct web fallback).",
                    dlUrl, validCsUrl, expectedAsset,
                    $"New version v{latestVer} is available (current: v{currentVer}).", IsError: false, IsCancelled: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog("Updater", $"Web fallback error: {ex.Message}");
                return null;
            }
        }

        public async Task<UpdateCheckResult> CheckForUpdateInfoAsync(string githubOwner, string repoName, CancellationToken token)
        {
            Emit("[UPDATER] Checking GitHub Releases for newer version...");
            string fallbackVer = GetAppVersionString();

            using var apiTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, apiTimeoutCts.Token);
            var ct = linkedCts.Token;

            try
            {
                var rawCurrent = Assembly.GetExecutingAssembly().GetName().Version;
                if (rawCurrent == null)
                    return new UpdateCheckResult(false, fallbackVer, fallbackVer, "", null, null, null, "Failed to read local assembly version.", IsError: true);

                var currentVer = NormalizeVersion3(rawCurrent);

                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{githubOwner}/{repoName}/releases/latest");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                HttpResponseMessage? response = null;
                try
                {
                    response = await UpdateHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    WriteDiagnosticLog("Updater", $"GitHub API request error: {ex.Message}");
                }

                if (response == null || !response.IsSuccessStatusCode)
                {
                    int statusCode = response != null ? (int)response.StatusCode : 0;
                    response?.Dispose();
                    WriteDiagnosticLog("Updater", $"GitHub API returned HTTP {statusCode}; trying web fallback...");

                    var webRes = await CheckForUpdateViaWebFallbackAsync(githubOwner, repoName, currentVer, ct).ConfigureAwait(false);
                    if (webRes != null)
                        return webRes;

                    string errHint = statusCode == 403 || statusCode == 429
                        ? "GitHub API rate limit reached (HTTP 403). Please try again shortly or switch VPN/IP."
                        : statusCode > 0
                            ? $"Could not check updates (GitHub HTTP {statusCode})."
                            : "Unable to reach GitHub. Please check your internet connection or proxy/VPN.";
                    return new UpdateCheckResult(false, currentVer.ToString(), currentVer.ToString(), "", null, null, null, errHint, IsError: true);
                }

                using (response)
                {
                    if (!IsTrustedGitHubUri(response.RequestMessage?.RequestUri))
                        return new UpdateCheckResult(false, currentVer.ToString(), currentVer.ToString(), "", null, null, null, "Blocked: GitHub API redirected to an untrusted host.", IsError: true);

                    string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string latestTag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
                    string notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

                    if (!TryParseReleaseTagVersion(latestTag, out var latestVer) || latestVer == null)
                        return new UpdateCheckResult(false, currentVer.ToString(), latestTag, "", null, null, null, $"Failed to parse GitHub release version tag ('{latestTag}').", IsError: true);

                    WriteDiagnosticLog("Updater", $"API latest tag={latestTag}, parsed={latestVer}, local={currentVer}");

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
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                WriteDiagnosticLog("Updater", "Update check cancelled by user.");
                return new UpdateCheckResult(false, fallbackVer, fallbackVer, "", null, null, null, "Update check was cancelled.", IsError: false, IsCancelled: true);
            }
            catch (OperationCanceledException)
            {
                WriteDiagnosticLog("Updater", "Update check timed out after 25s.");
                return new UpdateCheckResult(false, fallbackVer, fallbackVer, "", null, null, null, "Connection to GitHub timed out (25s). Please check your internet or VPN.", IsError: true, IsCancelled: false);
            }
            catch (Exception ex)
            {
                WriteDiagnosticLog("Updater", $"Update check exception: {ex.Message}");
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

            using var dlTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, dlTimeoutCts.Token);
            var ct = linkedCts.Token;

            try
            {
                progress?.Report(new UpdateDownloadProgress(0, 0, -1, 0, "PREPARING"));
                string lockedDir = await PrepareLockedUpdatesDirectoryAsync(ct).ConfigureAwait(false);
                stagedInstallerPath = Path.Combine(lockedDir, $"AionToolPro_Update_{update.LatestVersion}_{Guid.NewGuid():N}.exe");

                Emit($"[UPDATER] Downloading v{update.LatestVersion} into ACL-locked staging directory...");
                WriteDiagnosticLog("Updater", $"Starting download: {dlUri}");

                using (var dlResp = await UpdateHttp.GetAsync(dlUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!IsTrustedGitHubUri(dlResp.RequestMessage?.RequestUri))
                        return new UpdateInstallResult("ERROR", $"Blocked update: download redirected to an untrusted host ({dlResp.RequestMessage?.RequestUri?.Host}).");

                    dlResp.EnsureSuccessStatusCode();
                    long totalBytes = dlResp.Content.Headers.ContentLength ?? -1;
                    await using var src = await dlResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = new FileStream(stagedInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                    byte[] buffer = new byte[81920];
                    long readTotal = 0;
                    int lastPct = -1;
                    var sw = Stopwatch.StartNew();
                    long lastReportMs = -200;
                    int read;

                    while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
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

                    await dst.FlushAsync(ct).ConfigureAwait(false);
                    if (totalBytes > 0 && readTotal < totalBytes)
                    {
                        try { File.Delete(stagedInstallerPath); } catch { }
                        return new UpdateInstallResult("ERROR", $"Download ended prematurely ({readTotal:N0} / {totalBytes:N0} bytes). Please retry.");
                    }

                    progress?.Report(new UpdateDownloadProgress(100, readTotal, totalBytes > 0 ? totalBytes : readTotal, 0, "VERIFYING"));
                }

                byte[] magic = new byte[2];
                await using (var fs = File.OpenRead(stagedInstallerPath))
                {
                    if (fs.Length < 1024 || await fs.ReadAsync(magic.AsMemory(0, 2), ct).ConfigureAwait(false) < 2 || magic[0] != 0x4D || magic[1] != 0x5A)
                    {
                        try { File.Delete(stagedInstallerPath); } catch { }
                        return new UpdateInstallResult("ERROR", "Security Error: Downloaded file is not a valid Windows PE executable (missing MZ header).");
                    }
                }

                if (!string.IsNullOrWhiteSpace(update.ChecksumUrl) && IsTrustedGitHubUrl(update.ChecksumUrl, out var csUri) && csUri != null)
                {
                    Emit("[UPDATER] Verifying SHA-256 integrity checksum...");
                    using var csResp = await UpdateHttp.GetAsync(csUri, ct).ConfigureAwait(false);
                    if (!IsTrustedGitHubUri(csResp.RequestMessage?.RequestUri))
                    {
                        try { File.Delete(stagedInstallerPath); } catch { }
                        return new UpdateInstallResult("ERROR", "Blocked update: checksum URL redirected to an untrusted host.");
                    }
                    csResp.EnsureSuccessStatusCode();
                    string checksumText = await csResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    string actualHash;
                    await using (var fs = File.OpenRead(stagedInstallerPath))
                    {
                        byte[] hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
                        actualHash = Convert.ToHexString(hashBytes);
                    }

                    string? expectedHash = ExtractExpectedSha256(checksumText, update.AssetName);
                    if (!string.IsNullOrEmpty(expectedHash) && !expectedHash.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(stagedInstallerPath); } catch { }
                        string mismatch = $"Security Error: SHA-256 mismatch! Expected {expectedHash[..12]}..., got {actualHash[..12]}.... Update aborted.";
                        Emit($"[UPDATER ERR] {mismatch}");
                        WriteDiagnosticLog("Updater", mismatch);
                        return new UpdateInstallResult("ERROR", mismatch);
                    }
                    Emit("[UPDATER] PE header and SHA-256 checksum verified OK.");
                    WriteDiagnosticLog("Updater", $"SHA-256 verified OK: {actualHash}");
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
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!string.IsNullOrEmpty(stagedInstallerPath))
                    try { if (File.Exists(stagedInstallerPath)) File.Delete(stagedInstallerPath); } catch { }
                Emit("[UPDATER] Update download cancelled by user; temporary installer removed.");
                WriteDiagnosticLog("Updater", "Download cancelled by user.");
                return new UpdateInstallResult("CANCELLED", "Update download was cancelled cleanly and temporary files were removed.");
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrEmpty(stagedInstallerPath))
                    try { if (File.Exists(stagedInstallerPath)) File.Delete(stagedInstallerPath); } catch { }
                WriteDiagnosticLog("Updater", "Download timed out (15 min limit).");
                return new UpdateInstallResult("ERROR", "Download timed out after 15 minutes. Please check your connection and retry.");
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(stagedInstallerPath))
                    try { if (File.Exists(stagedInstallerPath)) File.Delete(stagedInstallerPath); } catch { }
                Emit($"[UPDATER ERR] {ex.Message}");
                WriteDiagnosticLog("Updater", $"Download exception: {ex.Message}");
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
