using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YTMusicLauncher.Models;

namespace YTMusicLauncher.Services
{
    public class YtDlpService
    {
        private string? _ytDlpPath;

        public YtDlpService()
        {
            _ytDlpPath = FindYtDlpPath();
        }

        public string GetYtDlpPath() => _ytDlpPath ?? "yt-dlp";

        public static string FindYtDlpPath()
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in pathEnv.Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(p.Trim(), "yt-dlp.exe");
                if (File.Exists(candidate)) return candidate;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                var matches = Directory.GetFiles(wingetPackages, "yt-dlp.exe", SearchOption.AllDirectories);
                if (matches.Length > 0) return matches[0];
            }

            var appDataTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTMusicLauncher", "yt-dlp.exe");
            if (File.Exists(appDataTool)) return appDataTool;

            return "yt-dlp.exe";
        }

        public async Task<bool> EnsureYtDlpExistsAsync(Action<string>? statusCallback = null)
        {
            if (File.Exists(_ytDlpPath) || _ytDlpPath == "yt-dlp")
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = _ytDlpPath,
                        Arguments = "--version",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    });
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode == 0) return true;
                    }
                }
                catch { }
            }

            statusCallback?.Invoke("Descargando ejecutable de yt-dlp...");
            try
            {
                var targetFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YTMusicLauncher");
                Directory.CreateDirectory(targetFolder);
                var targetFile = Path.Combine(targetFolder, "yt-dlp.exe");

                using var client = new HttpClient();
                var bytes = await client.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe");
                await File.WriteAllBytesAsync(targetFile, bytes);
                _ytDlpPath = targetFile;
                return true;
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke($"Error al descargar yt-dlp: {ex.Message}");
                return false;
            }
        }

        public void CancelDownload(DownloadItem item)
        {
            if (item == null) return;
            try
            {
                item.Cts?.Cancel();
                if (item.ActiveProcess != null && !item.ActiveProcess.HasExited)
                {
                    item.ActiveProcess.Kill(entireProcessTree: true);
                }

                // Also cancel any active sub-items
                foreach (var sub in item.SubItems)
                {
                    if (sub.CanSkip)
                    {
                        CancelSubItem(sub);
                    }
                }

                item.Status = DownloadStatus.Canceled;
                item.StatusText = "Descarga cancelada por el usuario";
                item.Speed = "";
                item.Eta = "";
            }
            catch { }
        }

        public void CancelSubItem(SubDownloadItem subItem)
        {
            if (subItem == null) return;
            try
            {
                subItem.Cts?.Cancel();
                if (subItem.ActiveProcess != null && !subItem.ActiveProcess.HasExited)
                {
                    subItem.ActiveProcess.Kill(entireProcessTree: true);
                }

                subItem.Status = DownloadStatus.Skipped;
                subItem.StatusText = "Skipeada por el usuario";

                // Cleanup incomplete files
                if (!string.IsNullOrWhiteSpace(subItem.OutputFilePath))
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(subItem.OutputFilePath);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            var incompleteFiles = Directory.GetFiles(dir, "*.part", SearchOption.AllDirectories);
                            foreach (var f in incompleteFiles) { try { File.Delete(f); } catch { } }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        public async Task<(string Title, List<(string Title, string Url)> Entries)> GetPlaylistInfoAsync(string url)
        {
            var result = (Title: "Playlist", Entries: new List<(string Title, string Url)>());
            try
            {
                var ytExe = GetYtDlpPath();
                var psi = new ProcessStartInfo
                {
                    FileName = ytExe,
                    Arguments = $"--flat-playlist -J \"{url}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return result;

                var json = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(json)) return result;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("title", out var titleProp))
                {
                    result.Title = titleProp.GetString() ?? "Playlist";
                }

                if (root.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entriesProp.EnumerateArray())
                    {
                        string eTitle = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "Canción" : "Canción";
                        string eUrl = entry.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(eUrl))
                        {
                            if (!eUrl.StartsWith("http"))
                            {
                                eUrl = $"https://www.youtube.com/watch?v={eUrl}";
                            }
                            result.Entries.Add((eTitle, eUrl));
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        public async Task DownloadAsync(DownloadItem item, Action? onCompleted = null)
        {
            item.Status = DownloadStatus.Downloading;
            item.StatusText = "Analizando enlace...";
            item.Progress = 0;
            item.Cts = new CancellationTokenSource();

            // Check if downloading playlist vs single track
            if (!item.SingleTrackOnly && (item.Url.Contains("playlist?list=") || item.Url.Contains("&list=")))
            {
                item.IsPlaylist = true;
                item.IsExpanded = true;
                item.StatusText = "Obteniendo lista de canciones...";

                var playlistInfo = await GetPlaylistInfoAsync(item.Url);
                if (!string.IsNullOrWhiteSpace(playlistInfo.Title))
                {
                    item.Title = playlistInfo.Title;
                }

                if (playlistInfo.Entries.Count > 0)
                {
                    int index = 1;
                    foreach (var entry in playlistInfo.Entries)
                    {
                        item.SubItems.Add(new SubDownloadItem
                        {
                            Index = index++,
                            Title = entry.Title,
                            Url = entry.Url,
                            StatusText = "En cola"
                        });
                    }

                    // Process sub items sequentially
                    int completed = 0;
                    int total = item.SubItems.Count;

                    for (int i = 0; i < item.SubItems.Count; i++)
                    {
                        if (item.Status == DownloadStatus.Canceled) break;

                        var sub = item.SubItems[i];
                        if (sub.Status == DownloadStatus.Skipped || sub.Status == DownloadStatus.Canceled) continue;

                        item.StatusText = $"Descargando canción {i + 1} de {total}... ({sub.Title})";

                        await DownloadSubItemAsync(sub, item.OutputDirectory, item.QualityFormat, item.Title);

                        if (sub.Status == DownloadStatus.Completed)
                        {
                            completed++;
                        }

                        item.Progress = ((double)(i + 1) / total) * 100.0;
                    }

                    if (item.Status != DownloadStatus.Canceled)
                    {
                        item.Status = DownloadStatus.Completed;
                        item.Progress = 100;
                        item.StatusText = $"Completado ({completed}/{total} canciones descargadas)";
                        onCompleted?.Invoke();
                    }
                    return;
                }
            }

            // Single Track Download
            await DownloadSingleTrackAsync(item, onCompleted);
        }

        private async Task DownloadSubItemAsync(SubDownloadItem subItem, string outputDirectory, string qualityFormat, string playlistTitle)
        {
            subItem.Status = DownloadStatus.Downloading;
            subItem.StatusText = "Iniciando...";
            subItem.Progress = 0;
            subItem.Cts = new CancellationTokenSource();

            var audioFormat = qualityFormat.Contains("M4A", StringComparison.OrdinalIgnoreCase) ? "m4a" :
                              qualityFormat.Contains("FLAC", StringComparison.OrdinalIgnoreCase) ? "flac" :
                              qualityFormat.Contains("WAV", StringComparison.OrdinalIgnoreCase) ? "wav" : "mp3";

            var playlistFolder = string.IsNullOrWhiteSpace(playlistTitle) ? "General" : playlistTitle;
            var outputTemplate = Path.Combine(outputDirectory, playlistFolder, "%(title)s [%(id)s].%(ext)s");
            subItem.OutputFilePath = outputTemplate;

            var arguments = $"--no-playlist -x --audio-format {audioFormat} --audio-quality 0 --embed-thumbnail --embed-metadata --newline --progress-template \"PROG|%(progress._percent_str)s\" -o \"{outputTemplate}\" \"{subItem.Url}\"";

            var ytExe = GetYtDlpPath();
            var psi = new ProcessStartInfo
            {
                FileName = ytExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            subItem.ActiveProcess = process;

            process.OutputDataReceived += (s, e) =>
            {
                if (subItem.Status == DownloadStatus.Skipped || subItem.Status == DownloadStatus.Canceled) return;
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                if (e.Data.StartsWith("PROG|"))
                {
                    var parts = e.Data.Split('|');
                    if (parts.Length >= 2 && double.TryParse(parts[1].Trim().Replace("%", ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    {
                        subItem.Progress = pct;
                        subItem.StatusText = $"Descargando {pct:F0}%";
                    }
                }
                else if (e.Data.Contains("[ExtractAudio]") || e.Data.Contains("[ffmpeg]"))
                {
                    subItem.Status = DownloadStatus.Converting;
                    subItem.StatusText = "Convertiendo audio...";
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(subItem.Cts.Token);

                if (subItem.Status == DownloadStatus.Skipped || subItem.Status == DownloadStatus.Canceled) return;

                if (process.ExitCode == 0)
                {
                    subItem.Status = DownloadStatus.Completed;
                    subItem.Progress = 100;
                    subItem.StatusText = "Completado";
                }
                else
                {
                    subItem.Status = DownloadStatus.Failed;
                    subItem.StatusText = "Error";
                }
            }
            catch (OperationCanceledException)
            {
                subItem.Status = DownloadStatus.Skipped;
                subItem.StatusText = "Skipeada";
            }
            catch
            {
                if (subItem.Status != DownloadStatus.Skipped && subItem.Status != DownloadStatus.Canceled)
                {
                    subItem.Status = DownloadStatus.Failed;
                    subItem.StatusText = "Error";
                }
            }
        }

        private async Task DownloadSingleTrackAsync(DownloadItem item, Action? onCompleted = null)
        {
            var audioFormat = item.QualityFormat.Contains("M4A", StringComparison.OrdinalIgnoreCase) ? "m4a" :
                              item.QualityFormat.Contains("FLAC", StringComparison.OrdinalIgnoreCase) ? "flac" :
                              item.QualityFormat.Contains("WAV", StringComparison.OrdinalIgnoreCase) ? "wav" : "mp3";

            var outputTemplate = Path.Combine(item.OutputDirectory, "General", "%(title)s [%(id)s].%(ext)s");

            var arguments = $"--no-playlist -x --audio-format {audioFormat} --audio-quality 0 --embed-thumbnail --embed-metadata --newline --progress-template \"PROG|%(progress._percent_str)s|%(progress._eta_str)s|%(progress._speed_str)s\" -o \"{outputTemplate}\" \"{item.Url}\"";

            var ytExe = GetYtDlpPath();
            var psi = new ProcessStartInfo
            {
                FileName = ytExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = psi };
            item.ActiveProcess = process;

            process.OutputDataReceived += (s, e) =>
            {
                if (item.Status == DownloadStatus.Canceled) return;
                if (string.IsNullOrWhiteSpace(e.Data)) return;

                if (e.Data.StartsWith("PROG|"))
                {
                    var parts = e.Data.Split('|');
                    if (parts.Length >= 4)
                    {
                        var pctStr = parts[1].Trim().Replace("%", "");
                        if (double.TryParse(pctStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            item.Progress = pct;
                        }
                        item.Eta = parts[2].Trim();
                        item.Speed = parts[3].Trim();
                        item.StatusText = $"Descargando... {pct:F1}% ({item.Speed})";
                    }
                }
                else if (e.Data.Contains("[ExtractAudio]") || e.Data.Contains("[ffmpeg]"))
                {
                    item.Status = DownloadStatus.Converting;
                    item.StatusText = "Convertiendo audio e incrustando portada...";
                }
                else if (e.Data.Contains("[download] Destination:") || e.Data.Contains("[download] Downloading item"))
                {
                    var titleMatch = Regex.Match(e.Data, @"Destination:\s*(.+)");
                    if (titleMatch.Success)
                    {
                        item.Title = Path.GetFileNameWithoutExtension(titleMatch.Groups[1].Value);
                    }
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(item.Cts.Token);

                if (item.Status == DownloadStatus.Canceled) return;

                if (process.ExitCode == 0)
                {
                    item.Status = DownloadStatus.Completed;
                    item.Progress = 100;
                    item.StatusText = "Completado";
                    onCompleted?.Invoke();
                }
                else
                {
                    item.Status = DownloadStatus.Failed;
                    item.StatusText = "Error en la descarga";
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadStatus.Canceled;
                item.StatusText = "Descarga cancelada por el usuario";
            }
            catch (Exception ex)
            {
                if (item.Status != DownloadStatus.Canceled)
                {
                    item.Status = DownloadStatus.Failed;
                    item.StatusText = $"Error: {ex.Message}";
                }
            }
        }
    }
}
