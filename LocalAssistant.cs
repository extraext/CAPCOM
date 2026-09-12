using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using KSP.UI.Screens;
using KSP.Localization;
using Contracts;

namespace LocalAssistant
{
    // =========================================================================
    // 1. SERVER LIFECYCLE MANAGER & PROCESS SUPERVISOR
    // =========================================================================
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class LocalAssistantPlugin : MonoBehaviour
    {
        public static Process serverProcess = null;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            KillOrphanedServers();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => UnloadModel();

            ModInspector.ScanInstalledMods();

            foreach (var model in ModelManager.Models)
            {
                if (model.IsDownloaded)
                {
                    ReloadServerWithModel(model.FileName);
                    break;
                }
            }
        }

        private static void KillOrphanedServers()
        {
            try
            {
                Process[] runningServers = Process.GetProcessesByName("llama-server");
                foreach (var p in runningServers)
                {
                    try { p.Kill(); p.Dispose(); } catch { }
                }
            }
            catch { }
        }

        public static void ReloadServerWithModel(string modelFileName)
        {
            UnloadModel();

            string kspRoot = AppDomain.CurrentDomain.BaseDirectory;
            string serverExe = Path.GetFullPath(Path.Combine(kspRoot, "LocalAssistant_Server", "llama-server.exe"));
            string modelPath = Path.GetFullPath(Path.Combine(kspRoot, "GameData", "LocalAssistant", "Models", modelFileName));

            if (!File.Exists(serverExe))
            {
                UnityEngine.Debug.LogWarning("[LocalAssistant] llama-server.exe missing in LocalAssistant_Server/");
                return;
            }

            if (!File.Exists(modelPath))
            {
                UnityEngine.Debug.LogWarning("[LocalAssistant] Model file not found: " + modelPath);
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = serverExe,
                    Arguments = "-m \"" + modelPath + "\" -c 4096 -ngl 99 --port 8080 --host 127.0.0.1",
                    WorkingDirectory = Path.GetDirectoryName(serverExe),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                serverProcess = Process.Start(psi);
                ModelManager.ActiveModelFileName = modelFileName;
                UnityEngine.Debug.Log("[LocalAssistant] Local AI Server started with: " + modelFileName);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[LocalAssistant] Failed to start server: " + ex.Message);
            }
        }

        public static void UnloadModel()
        {
            if (serverProcess != null && !serverProcess.HasExited)
            {
                try
                {
                    serverProcess.Kill();
                    serverProcess.Dispose();
                    serverProcess = null;
                    UnityEngine.Debug.Log("[LocalAssistant] llama-server process cleanly terminated.");
                }
                catch { }
            }
            ModelManager.ActiveModelFileName = "";
        }

        private void OnApplicationQuit()
        {
            UnloadModel();
            KillOrphanedServers();
        }
    }

    // =========================================================================
    // 2. MOD INSPECTOR
    // =========================================================================
    public static class ModInspector
    {
        public static string CachedModSummary = "";

        public static void ScanInstalledMods()
        {
            Task.Run(() =>
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("ACTIVE MODS DETECTED ON DISK IN GAMEDATA:");

                try
                {
                    string gameData = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData");
                    if (!Directory.Exists(gameData)) return;

                    string[] dirs = Directory.GetDirectories(gameData);
                    foreach (var dir in dirs)
                    {
                        string dirName = Path.GetFileName(dir);
                        if (dirName.Equals("Squad", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("SquadExpansion", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("LocalAssistant", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string modDescription = IdentifyKnownMod(dirName);
                        sb.AppendLine(string.Format("• {0}: {1}", dirName, modDescription));
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("Error scanning mods: " + ex.Message);
                }

                CachedModSummary = sb.ToString();
            });
        }

        private static string IdentifyKnownMod(string name)
        {
            string lower = name.ToLower();
            if (lower.Contains("environmentalvisualenhancements") || lower.Equals("eve") || lower.Equals("boulderco"))
                return "CLOUDS & ATMOSPHERE VISUAL MOD (Provides planetary cloud layers and weather effects).";
            if (lower.Contains("scatterer"))
                return "ATMOSPHERIC SCATTERING & WATER SHADER MOD (Realistic atmospheric haze, sunflares, ocean waves).";
            if (lower.Contains("parallax"))
                return "TERRAIN TESSELLATION MOD (Photorealistic surface rocks, 3D scatters, high-res textures).";
            if (lower.Contains("kopernicus"))
                return "PLANETARY SYSTEM FRAMEWORK (Core engine for custom planet packs and solar systems).";
            if (lower.Contains("ferramaerospace") || lower.Equals("far"))
                return "AERODYNAMIC REALISM OVERHAUL (Replaces stock aerodynamics with true aerodynamic equations).";
            if (lower.Contains("flightrecorder"))
                return "FLIGHT TELEMETRY RECORDER (Black-box flight data logger).";
            if (lower.Contains("enhancedcursor") || lower.Contains("enhancedcontrol") || lower.Contains("enhanceddynamics"))
                return "ENHANCED FLIGHT & UI CONTROL SUITE.";
            if (lower.Contains("cameratools"))
                return "CINEMATIC CAMERA SYSTEM (Tracking camera, flyby view, stationary cameras).";
            if (lower.Contains("tufx"))
                return "POST-PROCESSING & HDR SYSTEM (Color grading, bloom, ambient occlusion).";
            if (lower.Contains("realscaleboosters") || lower.Contains("realismoverhaul") || lower.Contains("ro"))
                return "REALISM OVERHAUL (Realistic engines, real fuels, and real-scale avionics).";
            if (lower.Contains("principia"))
                return "N-BODY GRAVITATION (Replaces patched conics with realistic N-body gravitational physics).";
            if (lower.Contains("kcalbeloh"))
                return "KCALBELOH PLANET SYSTEM (Black hole system with wormhole and exoplanets).";

            return "Active installed community modification.";
        }
    }

    // =========================================================================
    // 3. PERSISTENT CHAT ENGINE (BASE64 LOSSLESS CONFIGNODE STORAGE)
    // =========================================================================
    public class ChatMessageData
    {
        public string Sender = "";
        public string Text = "";
    }

    public class ChatSessionData
    {
        public string Id = "";
        public string Title = "";
        public string CreatedDate = "";
        public List<ChatMessageData> Messages = new List<ChatMessageData>();
    }

    public static class ChatStorage
    {
        private static string GetStorageFolder()
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameData", "LocalAssistant", "SavedChats");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return folder;
        }

        public static List<ChatSessionData> LoadAllSessions()
        {
            List<ChatSessionData> list = new List<ChatSessionData>();
            string folder = GetStorageFolder();
            string[] files = Directory.GetFiles(folder, "*.cfg");

            foreach (var file in files)
            {
                try
                {
                    ConfigNode node = ConfigNode.Load(file);
                    if (node != null && node.HasNode("CHAT_SESSION"))
                    {
                        ConfigNode sNode = node.GetNode("CHAT_SESSION");
                        ChatSessionData session = new ChatSessionData
                        {
                            Id = sNode.GetValue("id") ?? Path.GetFileNameWithoutExtension(file),
                            Title = sNode.GetValue("title") ?? "Untitled Chat",
                            CreatedDate = sNode.GetValue("createdDate") ?? "",
                            Messages = new List<ChatMessageData>()
                        };

                        ConfigNode[] msgNodes = sNode.GetNodes("MESSAGE");
                        foreach (var m in msgNodes)
                        {
                            string sender = m.GetValue("sender") ?? "System";
                            string base64Text = m.GetValue("payload") ?? "";
                            string decodedText = "";

                            if (!string.IsNullOrEmpty(base64Text))
                            {
                                byte[] bytes = Convert.FromBase64String(base64Text);
                                decodedText = Encoding.UTF8.GetString(bytes);
                            }
                            else
                            {
                                decodedText = m.GetValue("text") ?? "";
                            }

                            session.Messages.Add(new ChatMessageData
                            {
                                Sender = sender,
                                Text = decodedText
                            });
                        }

                        list.Add(session);
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning("[LocalAssistant] Error reading chat session: " + ex.Message);
                }
            }
            return list;
        }

        public static void SaveSession(ChatSessionData session)
        {
            if (session == null || string.IsNullOrEmpty(session.Id)) return;

            try
            {
                string path = Path.Combine(GetStorageFolder(), session.Id + ".cfg");
                ConfigNode root = new ConfigNode();
                ConfigNode sNode = root.AddNode("CHAT_SESSION");

                sNode.AddValue("id", session.Id);
                sNode.AddValue("title", session.Title);
                sNode.AddValue("createdDate", session.CreatedDate);

                if (session.Messages != null)
                {
                    foreach (var msg in session.Messages)
                    {
                        ConfigNode mNode = sNode.AddNode("MESSAGE");
                        mNode.AddValue("sender", msg.Sender);
                        string base64Text = Convert.ToBase64String(Encoding.UTF8.GetBytes(msg.Text ?? ""));
                        mNode.AddValue("payload", base64Text);
                    }
                }

                root.Save(path);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("[LocalAssistant] Failed to save chat session: " + ex.Message);
            }
        }

        public static void DeleteSession(string sessionId)
        {
            try
            {
                string path = Path.Combine(GetStorageFolder(), sessionId + ".cfg");
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    // =========================================================================
    // 4. MODEL MANAGER
    // =========================================================================
    public class ModelCard
    {
        public string Name;
        public string Description;
        public string VRAM;
        public string FileName;
        public string DownloadUrl;
        public long ExpectedSizeBytes;

        public bool IsDownloading = false;
        public bool CancelRequested = false;
        public float DownloadProgress = 0f;
        public string ProgressText = "";

        public HttpWebRequest ActiveRequest = null;

        public bool IsDownloaded => File.Exists(GetFilePath());

        public string GetFilePath()
        {
            string kspRoot = AppDomain.CurrentDomain.BaseDirectory;
            string folder = Path.Combine(kspRoot, "GameData", "LocalAssistant", "Models");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            return Path.Combine(folder, FileName);
        }

        public void CancelDownload()
        {
            CancelRequested = true;
            try
            {
                ActiveRequest?.Abort();
            }
            catch { }
        }
    }

    public static class ModelManager
    {
        static ModelManager()
        {
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
        }

        public static List<ModelCard> Models = new List<ModelCard>
        {
            new ModelCard
            {
                Name = "Qwen 2.5 3B Instruct",
                Description = "Responsive with decent accuracy, requires little amount of VRAM.",
                VRAM = "~2.2 GB VRAM",
                FileName = "qwen2.5-3b-instruct-q4_k_m.gguf",
                DownloadUrl = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
                ExpectedSizeBytes = 2210000000
            },

            new ModelCard
            {
                Name = "Gemma 4 E4B Instruct",
                Description = "Acceptable accuracy, requires little amount of VRAM.",
                VRAM = "~3.2 GB VRAM",
                FileName = "gemma-4-E4B-it-Q4_K_M.gguf",
                DownloadUrl = "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
                ExpectedSizeBytes = 4980000000
            },

            new ModelCard
            {
                Name = "Qwen 3.5 9B Instruct",
                Description = "Good accuracy, requires 6+ GB VRAM GPUs.",
                VRAM = "~5.8 GB VRAM",
                FileName = "Qwen3.5-9B-Q4_K_M.gguf",
                DownloadUrl = "https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf",
                ExpectedSizeBytes = 5680000000
            },

            new ModelCard
            {
                Name = "Gemma 4 12B QAT / IT",
                Description = "High accuracy, requires 8+ GB VRAM GPUs.",
                VRAM = "~7.4 GB VRAM",
                FileName = "gemma-4-12B-it-Q4_K_M.gguf",
                DownloadUrl = "https://huggingface.co/bartowski/gemma-4-12B-it-GGUF/resolve/main/gemma-4-12B-it-Q4_K_M.gguf",
                ExpectedSizeBytes = 7380000000
            },

            new ModelCard
            {
                Name = "Gemma 4 31B A4B QAT",
                Description = "Massive reasoning model, requires 16+ GB VRAM GPUs.",
                VRAM = "~18.2 GB VRAM",
                FileName = "gemma-4-31B-it-qat-UD-Q4_K_XL.gguf",
                DownloadUrl = "https://huggingface.co/unsloth/gemma-4-31B-it-qat-GGUF/resolve/main/gemma-4-31B-it-qat-UD-Q4_K_XL.gguf",
                ExpectedSizeBytes = 17000000000
            }
        };

        public static string ActiveModelFileName = "";

        public static async Task DownloadModelAsync(ModelCard card)
        {
            card.IsDownloading = true;
            card.CancelRequested = false;
            card.ProgressText = "Connecting...";

            string destinationPath = card.GetFilePath();
            string tempPath = destinationPath + ".download";

            await Task.Run(() =>
            {
                FileStream fs = null;
                Stream stream = null;
                HttpWebResponse response = null;

                try
                {
                    long existingLength = 0;
                    if (File.Exists(tempPath))
                    {
                        existingLength = new FileInfo(tempPath).Length;
                    }

                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(card.DownloadUrl);
                    request.Method = "GET";
                    request.Proxy = null; 

                    request.Timeout = 30000;
                    request.ReadWriteTimeout = 60000;
                    request.AllowAutoRedirect = true;
                    request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) KSPLocalAssistant/1.0";

                    if (existingLength > 0)
                    {
                        request.AddRange((int)existingLength);
                    }

                    card.ActiveRequest = request;

                    response = (HttpWebResponse)request.GetResponse();

                    long totalBytes;
                    bool isResuming = (response.StatusCode == HttpStatusCode.PartialContent);

                    if (isResuming)
                    {
                        totalBytes = existingLength + response.ContentLength;
                        fs = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None, 65536);
                    }
                    else
                    {
                        totalBytes = response.ContentLength > 0 ? response.ContentLength : card.ExpectedSizeBytes;
                        existingLength = 0;
                        fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                    }

                    stream = response.GetResponseStream();
                    byte[] buffer = new byte[65536];
                    long totalRead = existingLength;
                    int bytesRead;

                    while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (card.CancelRequested) break;

                        fs.Write(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        card.DownloadProgress = (float)totalRead / totalBytes;
                        double downloadedMB = totalRead / (1024.0 * 1024.0);
                        double totalMB = totalBytes / (1024.0 * 1024.0);
                        card.ProgressText = string.Format("{0:F1}% ({1:F0} MB / {2:F0} MB)", card.DownloadProgress * 100f, downloadedMB, totalMB);
                    }

                    fs.Flush();
                    fs.Close();
                    fs = null;

                    stream.Close();
                    stream = null;
                    response.Close();
                    response = null;

                    if (card.CancelRequested)
                    {
                        card.IsDownloading = false;
                        card.ProgressText = "Cancelled";
                        card.ActiveRequest = null;
                        return;
                    }

                    if (File.Exists(destinationPath)) File.Delete(destinationPath);
                    File.Move(tempPath, destinationPath);

                    card.ProgressText = "Download complete!";
                    card.IsDownloading = false;
                    card.ActiveRequest = null;

                    if (string.IsNullOrEmpty(ActiveModelFileName))
                    {
                        LocalAssistantPlugin.ReloadServerWithModel(card.FileName);
                    }
                }
                catch (WebException wex)
                {
                    if (fs != null) { try { fs.Close(); } catch { } }
                    if (stream != null) { try { stream.Close(); } catch { } }
                    if (response != null) { try { response.Close(); } catch { } }

                    card.IsDownloading = false;
                    card.ActiveRequest = null;

                    if (card.CancelRequested || wex.Status == WebExceptionStatus.RequestCanceled)
                    {
                        card.ProgressText = "Cancelled";
                    }
                    else
                    {
                        card.ProgressText = "Error: " + wex.Message;
                        UnityEngine.Debug.LogError("[LocalAssistant] Download network error: " + wex.Message);
                    }
                }
                catch (Exception ex)
                {
                    if (fs != null) { try { fs.Close(); } catch { } }
                    if (stream != null) { try { stream.Close(); } catch { } }
                    if (response != null) { try { response.Close(); } catch { } }

                    card.IsDownloading = false;
                    card.ActiveRequest = null;
                    card.ProgressText = "Error: " + ex.Message;
                    UnityEngine.Debug.LogError("[LocalAssistant] Download exception: " + ex.Message);
                }
            });
        }
    }

    // =========================================================================
    // 5. PERSISTENT SINGLETON UI
    // =========================================================================
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class LocalAssistantUI : MonoBehaviour
    {
        private static Rect windowRect = new Rect(Screen.width - 500, 75, 480, 560);
        public static bool IsVisible = false;

        private int currentTab = 0;
        private readonly string[] tabLabels = { "Chat", "Sessions", "Models" };

        public static ChatSessionData CurrentSession;
        public static List<ChatSessionData> SavedSessions = new List<ChatSessionData>();

        private string renamingSessionId = null;
        private string renameBuffer = "";

        private string inputBuffer = "";
        private Vector2 scrollPos;
        private Vector2 sessionScrollPos;
        private Vector2 modelScrollPos;
        public static bool IsBusy = false;
        private static bool shouldScrollToBottom = false;

        private bool isResizing = false;
        private Vector2 resizeStartMouse;
        private Vector2 resizeStartDimensions;

        private ApplicationLauncherButton toolbarButton;
        private const string ControlLockID = "LocalAssistant_InputLock";
        private const string ScrollLockID = "LocalAssistant_ScrollLock";
        private bool isInputLocked = false;
        private bool isCameraLocked = false;

        private Font smoothFont;
        private GUIStyle localAssistantTextStyle;
        private GUIStyle pilotTextStyle;
        private GUIStyle localAssistantCardStyle;
        private GUIStyle pilotCardStyle;
        private GUIStyle modelCardStyle;
        private GUIStyle reasoningStyle;
        private GUIStyle textAreaStyle;
        private GUIStyle sendButtonStyle;
        private GUIStyle sessionCardStyle;
        private GUIStyle sessionRowButtonStyle;
        private GUIStyle deleteButtonStyle;
        private GUIStyle renameButtonStyle;
        private GUIStyle resizeGripStyle;
        private bool stylesInitialized = false;

        private static readonly string[] WelcomeGreetings = 
        {
            "LocalAssistant online. Standing by.",
            "LocalAssistant on the loop. What are we launching today?",
            "LocalAssistant here. Ready when you are.",
            "Radio link confirmed. What's the flight plan?",
            "Telemetry and systems nominal. What's our next maneuver?"
        };

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(OnAppLauncherReady);
            if (ApplicationLauncher.Ready) OnAppLauncherReady();

            SavedSessions = ChatStorage.LoadAllSessions();

            if (CurrentSession == null)
            {
                CreateNewSession();
            }
        }

        private static void CreateNewSession()
        {
            string greeting = WelcomeGreetings[UnityEngine.Random.Range(0, WelcomeGreetings.Length)];
            CurrentSession = new ChatSessionData
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "Chat " + DateTime.Now.ToString("MMM dd HH:mm"),
                CreatedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Messages = new List<ChatMessageData>()
            };
            CurrentSession.Messages.Add(new ChatMessageData
            {
                Sender = "LocalAssistant",
                Text = greeting
            });
            shouldScrollToBottom = true;
        }

        private void OnAppLauncherReady()
        {
            if (toolbarButton == null && ApplicationLauncher.Instance != null)
            {
                Texture2D iconTex = GameDatabase.Instance.GetTexture("LocalAssistant/Textures/icon", false) ?? Texture2D.whiteTexture;

                toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                    onTrue: () => IsVisible = true,
                    onFalse: () => { IsVisible = false; ReleaseAllLocks(); },
                    onHover: null, onHoverOut: null, onEnable: null, onDisable: null,
                    visibleInScenes: ApplicationLauncher.AppScenes.FLIGHT | 
                                     ApplicationLauncher.AppScenes.VAB | 
                                     ApplicationLauncher.AppScenes.SPH | 
                                     ApplicationLauncher.AppScenes.SPACECENTER | 
                                     ApplicationLauncher.AppScenes.TRACKSTATION | 
                                     ApplicationLauncher.AppScenes.MAINMENU,
                    texture: iconTex
                );
            }
        }

        private void Update()
        {
            if (IsVisible)
            {
                Vector2 mousePos = new Vector2(UnityEngine.Input.mousePosition.x, Screen.height - UnityEngine.Input.mousePosition.y);
                bool isMouseOverWindow = windowRect.Contains(mousePos);

                if (isMouseOverWindow && !isCameraLocked)
                {
                    InputLockManager.SetControlLock(ControlTypes.CAMERACONTROLS, ScrollLockID);
                    isCameraLocked = true;
                }
                else if (!isMouseOverWindow && isCameraLocked)
                {
                    InputLockManager.RemoveControlLock(ScrollLockID);
                    isCameraLocked = false;
                }

                if (isResizing)
                {
                    float deltaX = UnityEngine.Input.mousePosition.x - resizeStartMouse.x;
                    float deltaY = resizeStartMouse.y - UnityEngine.Input.mousePosition.y;

                    windowRect.width = Mathf.Clamp(resizeStartDimensions.x + deltaX, 380, Screen.width - 40);
                    windowRect.height = Mathf.Clamp(resizeStartDimensions.y + deltaY, 360, Screen.height - 40);

                    if (UnityEngine.Input.GetMouseButtonUp(0))
                    {
                        isResizing = false;
                    }
                }
            }
            else if (isCameraLocked)
            {
                InputLockManager.RemoveControlLock(ScrollLockID);
                isCameraLocked = false;
            }
        }

        private void InitStyles()
        {
            smoothFont = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");

            localAssistantTextStyle = new GUIStyle(GUI.skin.label)
            {
                font = smoothFont,
                wordWrap = true,
                richText = true,
                fontSize = 12,
                normal = { textColor = new Color(0.92f, 0.94f, 0.96f) }
            };

            pilotTextStyle = new GUIStyle(GUI.skin.label)
            {
                font = smoothFont,
                wordWrap = true,
                richText = true,
                fontSize = 12,
                normal = { textColor = new Color(0.95f, 0.98f, 1f) }
            };

            pilotCardStyle = new GUIStyle(GUI.skin.box)
            {
                font = smoothFont,
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 45, 4, 4)
            };

            localAssistantCardStyle = new GUIStyle(GUI.skin.box)
            {
                font = smoothFont,
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(45, 0, 4, 4)
            };

            modelCardStyle = new GUIStyle(GUI.skin.box)
            {
                font = smoothFont,
                padding = new RectOffset(10, 10, 10, 10),
                margin = new RectOffset(4, 4, 3, 3)
            };

            sessionCardStyle = new GUIStyle(GUI.skin.box)
            {
                font = smoothFont,
                padding = new RectOffset(6, 6, 6, 6),
                margin = new RectOffset(0, 0, 3, 3)
            };

            sessionRowButtonStyle = new GUIStyle(GUI.skin.button)
            {
                font = smoothFont,
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 4, 4)
            };

            deleteButtonStyle = new GUIStyle(GUI.skin.button)
            {
                font = smoothFont,
                fontSize = 11,
                normal = { textColor = new Color(1f, 0.45f, 0.45f) }
            };

            renameButtonStyle = new GUIStyle(GUI.skin.button)
            {
                font = smoothFont,
                fontSize = 11,
                normal = { textColor = new Color(0.6f, 0.85f, 1f) }
            };

            reasoningStyle = new GUIStyle(GUI.skin.label)
            {
                font = smoothFont,
                fontSize = 11,
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.45f, 0.85f, 0.6f) }
            };

            textAreaStyle = new GUIStyle(GUI.skin.textArea)
            {
                font = smoothFont,
                fontSize = 12,
                wordWrap = true,
                padding = new RectOffset(6, 6, 5, 5)
            };

            sendButtonStyle = new GUIStyle(GUI.skin.button)
            {
                font = smoothFont,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            resizeGripStyle = new GUIStyle(GUI.skin.label)
            {
                font = smoothFont,
                fontSize = 10,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f, 0.7f) }
            };

            stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!IsVisible) return;
            if (!stylesInitialized) InitStyles();

            GUI.skin = HighLogic.Skin;
            windowRect = GUILayout.Window(845920, windowRect, DrawWindow, "LocalAssistant", GUILayout.MinWidth(380), GUILayout.MinHeight(360));
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            // Header Toolbar
            GUILayout.BeginHorizontal();
            currentTab = GUILayout.Toolbar(currentTab, tabLabels, GUILayout.Height(26));
            if (GUILayout.Button("+ New", GUILayout.Width(55), GUILayout.Height(26)))
            {
                CreateNewSession();
                currentTab = 0;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            if (currentTab == 0)
            {
                DrawChatTerminal();
            }
            else if (currentTab == 1)
            {
                DrawSessionsManager();
            }
            else
            {
                DrawModelsManager();
            }

            GUILayout.EndVertical();

            // Corner Resize Grip
            Rect gripRect = new Rect(windowRect.width - 14, windowRect.height - 14, 12, 12);
            GUI.Label(gripRect, "◢", resizeGripStyle);

            if (Event.current.type == EventType.MouseDown && gripRect.Contains(Event.current.mousePosition))
            {
                isResizing = true;
                resizeStartMouse = UnityEngine.Input.mousePosition;
                resizeStartDimensions = new Vector2(windowRect.width, windowRect.height);
                Event.current.Use();
            }

            GUI.DragWindow(new Rect(0, 0, windowRect.width - 24, 25));
        }

        private void DrawChatTerminal()
        {
            if (CurrentSession == null) CreateNewSession();
            if (CurrentSession.Messages == null) CurrentSession.Messages = new List<ChatMessageData>();

            scrollPos = GUILayout.BeginScrollView(scrollPos, GUILayout.ExpandHeight(true));
            foreach (var msg in CurrentSession.Messages)
            {
                bool isAssistant = msg.Sender == "LocalAssistant";
                GUILayout.BeginVertical(isAssistant ? localAssistantCardStyle : pilotCardStyle);

                if (isAssistant)
                    GUILayout.Label("<color=#38ef7d><b>✦ LocalAssistant</b></color>", localAssistantTextStyle);
                else
                    GUILayout.Label("<color=#4facfe><b>You</b></color>", pilotTextStyle);

                GUILayout.Space(2);
                string formattedText = MarkdownToRichText(msg.Text);
                GUILayout.Label(formattedText, isAssistant ? localAssistantTextStyle : pilotTextStyle);
                GUILayout.EndVertical();
            }

            if (IsBusy)
            {
                GUILayout.BeginVertical(localAssistantCardStyle);
                GUILayout.Label("<color=#38ef7d>✦</color> <i>Thinking...</i>", reasoningStyle);
                GUILayout.EndVertical();
            }

            if (shouldScrollToBottom)
            {
                scrollPos.y = float.MaxValue;
                if (Event.current.type == EventType.Repaint) shouldScrollToBottom = false;
            }

            GUILayout.EndScrollView();
            GUILayout.Space(4);

            // Dynamic Input Bar
            GUILayout.BeginHorizontal();

            GUIContent textContent = new GUIContent(string.IsNullOrEmpty(inputBuffer) ? " " : inputBuffer);
            float calculatedHeight = textAreaStyle.CalcHeight(textContent, windowRect.width - 64);
            float dynamicBoxHeight = Mathf.Clamp(calculatedHeight + 4, 26f, 75f);

            GUI.SetNextControlName("PromptInputField");
            inputBuffer = GUILayout.TextArea(inputBuffer, textAreaStyle, GUILayout.Height(dynamicBoxHeight), GUILayout.ExpandWidth(true));

            if (GUI.GetNameOfFocusedControl() == "PromptInputField")
            {
                if (!isInputLocked) ApplyInputLock();
            }
            else
            {
                if (isInputLocked) ReleaseInputLock();
            }

            bool enterPressed = Event.current.isKey && Event.current.keyCode == KeyCode.Return && !Event.current.shift;
            bool sendClicked = GUILayout.Button("➤", sendButtonStyle, GUILayout.Width(34), GUILayout.Height(dynamicBoxHeight));

            if ((sendClicked || enterPressed) && !string.IsNullOrWhiteSpace(inputBuffer) && !IsBusy)
            {
                string query = inputBuffer.Trim();
                inputBuffer = "";
                GUI.FocusControl(null);
                ReleaseInputLock();
                SendPrompt(query);
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(2);
        }

        private void DrawSessionsManager()
        {
            GUILayout.Label("<b>Saved Conversations</b>", localAssistantTextStyle);
            GUILayout.Space(4);

            sessionScrollPos = GUILayout.BeginScrollView(sessionScrollPos, GUILayout.ExpandHeight(true));

            if (SavedSessions == null || SavedSessions.Count == 0)
            {
                GUILayout.Label("<color=#888888>No saved sessions found.</color>", localAssistantTextStyle);
            }
            else
            {
                for (int i = SavedSessions.Count - 1; i >= 0; i--)
                {
                    var s = SavedSessions[i];
                    if (s == null) continue;

                    GUILayout.BeginVertical(sessionCardStyle);
                    GUILayout.BeginHorizontal();

                    bool isCurrent = CurrentSession != null && s.Id == CurrentSession.Id;

                    if (renamingSessionId == s.Id)
                    {
                        renameBuffer = GUILayout.TextField(renameBuffer, GUILayout.ExpandWidth(true));
                        if (GUILayout.Button("✓", GUILayout.Width(24), GUILayout.Height(22)))
                        {
                            if (!string.IsNullOrWhiteSpace(renameBuffer))
                            {
                                s.Title = renameBuffer.Trim();
                                ChatStorage.SaveSession(s);
                            }
                            renamingSessionId = null;
                        }
                        if (GUILayout.Button("✕", GUILayout.Width(24), GUILayout.Height(22)))
                        {
                            renamingSessionId = null;
                        }
                    }
                    else
                    {
                        string titleText = (isCurrent ? "<color=#38ef7d>●</color> " : "") + s.Title;
                        if (GUILayout.Button(titleText, sessionRowButtonStyle, GUILayout.ExpandWidth(true)))
                        {
                            CurrentSession = s;
                            if (CurrentSession.Messages == null) CurrentSession.Messages = new List<ChatMessageData>();
                            currentTab = 0;
                            shouldScrollToBottom = true;
                            scrollPos = new Vector2(0, float.MaxValue);
                        }

                        if (GUILayout.Button("✎", renameButtonStyle, GUILayout.Width(24), GUILayout.Height(22)))
                        {
                            renamingSessionId = s.Id;
                            renameBuffer = s.Title;
                        }

                        if (GUILayout.Button("✕", deleteButtonStyle, GUILayout.Width(24), GUILayout.Height(22)))
                        {
                            ChatStorage.DeleteSession(s.Id);
                            SavedSessions.RemoveAt(i);
                            if (CurrentSession != null && CurrentSession.Id == s.Id) CreateNewSession();
                            break;
                        }
                    }

                    GUILayout.EndHorizontal();
                    int msgCount = s.Messages != null ? s.Messages.Count : 0;
                    GUILayout.Label("<size=10><color=#778899>" + s.CreatedDate + " • " + msgCount + " msgs</color></size>", localAssistantTextStyle);
                    GUILayout.EndVertical();
                }
            }

            GUILayout.EndScrollView();
        }

        private void DrawModelsManager()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(2);

            modelScrollPos = GUILayout.BeginScrollView(modelScrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            foreach (var model in ModelManager.Models)
            {
                GUILayout.BeginVertical(modelCardStyle);

                GUILayout.BeginHorizontal();
                GUILayout.Label("<b>" + model.Name + "</b>", localAssistantTextStyle);
                GUILayout.Label("<color=#66ccff>" + model.VRAM + "</color>", localAssistantTextStyle, GUILayout.Width(95));
                GUILayout.EndHorizontal();

                GUILayout.Label("<size=11><color=#a0a8b0>" + model.Description + "</color></size>", localAssistantTextStyle);
                GUILayout.Space(6);

                if (model.IsDownloading)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Downloading: " + model.ProgressText, reasoningStyle);
                    
                    if (GUILayout.Button("✕ Cancel", deleteButtonStyle, GUILayout.Width(65), GUILayout.Height(20)))
                    {
                        model.CancelDownload();
                    }
                    GUILayout.EndHorizontal();

                    DrawProgressBar(model.DownloadProgress);
                }
                else if (model.IsDownloaded)
                {
                    GUILayout.BeginHorizontal();
                    bool isActive = ModelManager.ActiveModelFileName == model.FileName;

                    if (isActive)
                    {
                        GUILayout.Label("<color=#55ff55><b>● LOADED</b></color>", localAssistantTextStyle, GUILayout.ExpandWidth(true));
                        
                        if (GUILayout.Button("Unload", GUILayout.Width(80), GUILayout.Height(24)))
                        {
                            LocalAssistantPlugin.UnloadModel();
                        }
                    }
                    else
                    {
                        GUILayout.Label("<color=#888888>Installed</color>", localAssistantTextStyle, GUILayout.ExpandWidth(true));
                        if (GUILayout.Button("Load Model", GUILayout.Width(90), GUILayout.Height(24)))
                        {
                            LocalAssistantPlugin.ReloadServerWithModel(model.FileName);
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("<color=#ffaa55>Not Installed</color>", localAssistantTextStyle, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Download", GUILayout.Width(100), GUILayout.Height(24)))
                    {
                        _ = ModelManager.DownloadModelAsync(model);
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.EndVertical();
                GUILayout.Space(4);
            }

            GUILayout.EndScrollView();
            GUILayout.Space(2);
            GUILayout.EndHorizontal();
        }

        private void DrawProgressBar(float progress)
        {
            Rect barRect = GUILayoutUtility.GetRect(100, 14);
            GUI.Box(barRect, "");
            Rect fillRect = new Rect(barRect.x, barRect.y, barRect.width * Mathf.Clamp01(progress), barRect.height);
            GUI.DrawTexture(fillRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, new Color(0.2f, 0.85f, 0.4f), 0, 0);
        }

        private async void SendPrompt(string prompt)
        {
            if (string.IsNullOrEmpty(ModelManager.ActiveModelFileName))
            {
                CurrentSession.Messages.Add(new ChatMessageData
                {
                    Sender = "LocalAssistant",
                    Text = "No AI model loaded! Click the 'Models' tab to download or load a model."
                });
                shouldScrollToBottom = true;
                return;
            }

            IsBusy = true;
            CurrentSession.Messages.Add(new ChatMessageData { Sender = "You", Text = prompt });

            if (CurrentSession.Messages.Count == 2)
            {
                CurrentSession.Title = prompt.Length > 24 ? prompt.Substring(0, 24) + "..." : prompt;
            }

            shouldScrollToBottom = true;

            string response = await AIService.QueryLocalAssistant(CurrentSession.Messages);

            CurrentSession.Messages.Add(new ChatMessageData { Sender = "LocalAssistant", Text = response });
            IsBusy = false;
            shouldScrollToBottom = true;

            ChatStorage.SaveSession(CurrentSession);
            SavedSessions = ChatStorage.LoadAllSessions();
        }

        public static string MarkdownToRichText(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";

            md = Regex.Replace(md, @"^###\s+(.*)$", "<b><size=13><color=#88ccff>$1</color></size></b>", RegexOptions.Multiline);
            md = Regex.Replace(md, @"^##\s+(.*)$", "<b><size=14><color=#88ccff>$1</color></size></b>", RegexOptions.Multiline);
            md = Regex.Replace(md, @"^#\s+(.*)$", "<b><size=15><color=#88ccff>$1</color></size></b>", RegexOptions.Multiline);

            md = Regex.Replace(md, @"\*\*(.*?)\*\*", "<b>$1</b>");
            md = Regex.Replace(md, @"__(.*?)__", "<b>$1</b>");

            md = Regex.Replace(md, @"\*(.*?)\*", "<i>$1</i>");
            md = Regex.Replace(md, @"_(.*?)_", "<i>$1</i>");

            md = Regex.Replace(md, @"`([^`]+)`", "<color=#ffaa55><b>$1</b></color>");
            md = Regex.Replace(md, @"```[\w]*\n([\s\S]*?)```", "<color=#ffaa55>$1</color>");
            md = Regex.Replace(md, @"^[\*\-]\s+(.*)$", "  • $1", RegexOptions.Multiline);

            return md;
        }

        private void ApplyInputLock()
        {
            InputLockManager.SetControlLock(ControlTypes.ALL_SHIP_CONTROLS, ControlLockID);
            isInputLocked = true;
        }

        private void ReleaseInputLock()
        {
            InputLockManager.RemoveControlLock(ControlLockID);
            isInputLocked = false;
        }

        private void ReleaseAllLocks()
        {
            ReleaseInputLock();
            if (isCameraLocked)
            {
                InputLockManager.RemoveControlLock(ScrollLockID);
                isCameraLocked = false;
            }
        }

        private void OnDisable()
        {
            ReleaseAllLocks();
        }

        private void OnDestroy()
        {
            ReleaseAllLocks();
            if (toolbarButton != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
                toolbarButton = null;
            }
        }
    }

    // =========================================================================
    // 6. HTTP AI SERVICE (ENCYCLOPEDIC KSP MASTER COMPENDIUM)
    // =========================================================================
    public static class AIService
    {
        private const string ServerUrl = "http://127.0.0.1:8080/v1/chat/completions";

        public static async Task<string> QueryLocalAssistant(List<ChatMessageData> conversationHistory)
        {
            return await Task.Run(() =>
            {
                if (LocalAssistantPlugin.serverProcess != null && LocalAssistantPlugin.serverProcess.HasExited)
                {
                    return "[Server Crash]: llama-server.exe exited with code " + LocalAssistantPlugin.serverProcess.ExitCode + ". Check GPU drivers or DLLs.";
                }

                try
                {
                    string installedMods = ModInspector.CachedModSummary;
                    string sceneContext = KSPKnowledgeBase.GetCurrentSceneContext();
                    string telemetry = TelemetryHarvester.GetActiveVesselTelemetry();
                    string activeBodyPhysics = KSPKnowledgeBase.GetDynamicActiveBodyData();
                    string contracts = KSPKnowledgeBase.GetActiveContractsSummary();

                    string kspMasterInfo =
                        "=== MASTER KERBAL SPACE PROGRAM ENCYCLOPEDIA & COGNITIVE RULES ===\n" +
                        "You are LocalAssistant, the technical flight specialist and mission companion running directly inside the player's KSP process.\n\n" +

                        "CRITICAL ACCESS DIRECTIVES:\n" +
                        "1. YOU HAVE DIRECT LIVE DISK & TELEMETRY ACCESS: You are an internal plugin running inside KSP with direct read access to GameData and vessel telemetry. NEVER say 'I don't have access to your filesystem', 'I am a text-based AI', or 'Here is a general example list'. The data provided below is real, local, and live.\n" +
                        "2. MOD IDENTIFICATION: EnvironmentalVisualEnhancements (EVE) and BoulderCo ARE the player's cloud and weather mods. Scatterer is their atmosphere/water shader mod. Parallax is their terrain tessellation mod. FAR is their aerodynamics mod. When asked what mods they have or if they have a cloud mod, state the answer directly and accurately from their mod list below.\n\n" +

                        "3. REAL KSP SCENE DEFINITIONS:\n" +
                        "   - MAINMENU: The starting title screen (Resume Game, New Game, Training, Scenarios, Settings, Quit). You do NOT manage Kerbals, build ships, or choose planets here!\n" +
                        "   - SPACECENTER: The Kerbal Space Center hub (VAB, SPH, Tracking Station, Launchpad, Runway, Astronaut Complex for hiring/managing Kerbals, Mission Control for contracts, R&D for tech tree, Admin Building).\n" +
                        "   - EDITOR: VAB (Rockets) or SPH (Spaceplanes) for vehicle construction.\n" +
                        "   - FLIGHT: Active flight scene piloting a vessel in atmosphere, orbit, or on a surface.\n" +
                        "   - TRACKINGSTATION: Orbital map overview to track, recover, or switch between all active vessels, debris, and celestial bodies.\n\n" +

                        "4. LOSS-OF-CONTROL EMERGENCY PLAYBOOK:\n" +
                        "   When a player says keys don't work, RCS won't fire, or they have no control, diagnose real KSP causes:\n" +
                        "   - CAUSE 1 (Most common): ELECTRIC CHARGE IS 0! Check top-right Resources icon. Reaction wheels, probe cores, and avionics die instantly when EC runs out. Solution: Deploy solar panels, face the sun, or fire an engine with an alternator.\n" +
                        "   - CAUSE 2: COMMNET SIGNAL OCCLUDED / PROBE LOST CONNECTION! Unmanned probe cores need a line-of-sight signal back to KSC or a relay satellite. If behind a celestial body or out of range, control locks up.\n" +
                        "   - CAUSE 3: TIME WARP ACTIVE! Physics warp or non-physics warp freezes or restricts control inputs. Press comma (,) or slash (/) to return to 1x normal time.\n" +
                        "   - CAUSE 4: CONTROLS LOCKED! Alt+L toggles staging/control lock. Check if control needles at bottom-left are locked.\n" +
                        "   - CAUSE 5: RCS HAS NO THROTTLE! RCS does NOT have a throttle. Z and X only throttle main liquid/solid rocket engines. RCS is purely translational (H/N/I/J/K/L) and attitude assist (R key).\n\n" +

                        "5. REAL KSP FLIGHT CONTROLS & INTERFACE:\n" +
                        "   - Pitch: W (Nose Down), S (Nose Up) | Yaw: A (Left), D (Right) | Roll: Q (Left), E (Right)\n" +
                        "   - Main Engine Throttle: Left Shift (Up), Left Ctrl (Down), Z (Instant 100%), X (Instant 0% Cut)\n" +
                        "   - Staging: Spacebar (Fires current stage)\n" +
                        "   - SAS Assist: T key | RCS Thrusters: R key | Precision Mode: Caps Lock\n" +
                        "   - Navball Speed Header: Clicking the speed number directly on top of the Navball toggles between Surface, Orbit, and Target speed modes.\n" +
                        "   - NO FAKE AUTOPILOT: Stock KSP has NO 'Fly to Orbit' right-click autopilot. All flight is manual using WASD/QE, Navball vectors, and maneuver nodes (M key).\n\n" +

                        "6. ASTRODYNAMICS, TRANSFERS & CELESTIAL MECHANICS:\n" +
                        "   - INTERPLANETARY PHASE ANGLES (From Kerbin):\n" +
                        "     * Duna: +44.4° (Duna leads Kerbin by ~44°)\n" +
                        "     * Eve: -54.1° (Eve trails behind Kerbin by ~54°)\n" +
                        "     * Jool: +96.7° ahead | Dres: +82.1° ahead | Eeloo: +101.4° ahead | Moho: -251.8°\n" +
                        "   - DELTA-V BUDGETS: Kerbin LKO (3,400 m/s) | Mun (Transfer 860, Land 580, Return 310) | Minmus (Transfer 930, Land 180, Return 160) | Duna (Transfer 1080, Capture 610, Land 500, Orbit 1450) | Eve (Orbit to surface free w/ chutes, Sea-level ascent 8,000 m/s) | Tylo (Landing 2,270 m/s, Ascent 2,270 m/s, NO ATMOSPHERE, TWR > 1 required!)\n" +
                        "   - RENDEZVOUS & DOCKING: Match inclinations at An/Dn -> intercept node (< 2km) -> switch Navball to 'Target' mode at closest approach -> burn Retrograde until relative speed is 0.0 m/s -> burn towards Target marker (< 10 m/s) -> within 50m, use RCS translation (H/N/I/J/K/L) -> right-click docking port and select 'Control From Here' -> magnets clamp automatically when within 2m.\n" +
                        "   - ATMOSPHERIC RE-ENTRY: Retrograde burn to set periapsis inside atmosphere (Kerbin: 30-35km from LKO, 25-30km from Mun; Duna: 18-22km). Keep heat shield pointed retrograde inside shockwave cone; ablator sheds heat automatically.\n" +
                        "   - PARACHUTE PHYSICS: Drogue chutes (orange) open at high speeds (up to 500 m/s) to slow craft down. Main chutes (blue/white) rip off if deployed above ~270 m/s on Kerbin. On Duna, set min pressure to 0.04 atm and deploy altitude to 3,000m due to thin air.\n\n" +

                        "7. PROPULSION, POWER & THERMAL SYSTEMS:\n" +
                        "   - LFO RATIO: Standard LiquidFuel + Oxidizer ratio is exactly 9:11 (45% LF / 55% Ox).\n" +
                        "   - NUCLEAR ENGINES: LV-N 'Nerv' burns ONLY LiquidFuel! Never bring Oxidizer tanks for Nervs (empty oxidizer to save mass).\n" +
                        "   - POWER GENERATION: Solar panels lose power by 1/r² (Jool has only 4% solar power; Eeloo has 1%). Outer solar system missions require RTGs (PB-NUK, steady 0.75 EC/s) or Fuel Cells (burning LFO to generate continuous EC).\n" +
                        "   - RADIATORS: Deployable folding radiators (TCS) cool the entire vessel and internal parts from anywhere on the craft. Static radiator panels only cool parts directly attached to them. Drills and Convert-O-Trons overheat and throttle down without active cooling.\n\n" +

                        "8. KERBAL ROLES & CAREER PROGRESSION:\n" +
                        "   - PILOTS: Provide SAS stability hold and directional vector hold modes (Prograde, Retrograde, Normal, Radial, Target).\n" +
                        "   - SCIENTISTS: Restore inoperable Mystery Goo and Materials Bays on EVA; staff Mobile Processing Labs (MPL) to multiply science output by 5x.\n" +
                        "   - ENGINEERS: Repack deployed parachutes on EVA, repair broken rover wheels and landing legs, and dramatically boost ISRU mining drill extraction rates.\n" +
                        "   - CAREER BUILDINGS: Maneuver nodes require BOTH Tracking Station Tier 2 and Mission Control Tier 2. Space EVAs require Astronaut Complex Tier 2.\n\n" +

                        "LIVE LOCAL TELEMETRY & SYSTEM STATE:\n" +
                        activeBodyPhysics + "\n" +
                        sceneContext + "\n" +
                        installedMods + "\n" +
                        contracts + "\n" +
                        telemetry + "\n\n" +

                        "CONVERSATIONAL STYLE:\n" +
                        "- Talk naturally, casually, and intelligently like an experienced spaceflight friend. Never use military tropes like 'Pilot' or 'Commander'.\n" +
                        "- Use clean Markdown formatting.\n" +
                        "- Identity: You are LocalAssistant. Never identify as Qwen, Gemma, Alibaba, or Google.";

                    StringBuilder messagesJson = new StringBuilder();
                    messagesJson.Append("{\"role\": \"system\", \"content\": \"" + EscapeJson(kspMasterInfo) + "\"}");

                    int startIndex = Math.Max(0, conversationHistory.Count - 10);
                    for (int i = startIndex; i < conversationHistory.Count; i++)
                    {
                        var msg = conversationHistory[i];
                        string role = msg.Sender == "You" ? "user" : "assistant";
                        messagesJson.Append(",{\"role\": \"" + role + "\", \"content\": \"" + EscapeJson(msg.Text) + "\"}");
                    }

                    string jsonPayload = "{" +
                        "\"model\": \"local-model\"," +
                        "\"messages\": [" + messagesJson.ToString() + "]," +
                        "\"temperature\": 0.90," +
                        "\"max_tokens\": 600" +
                    "}";

                    byte[] postBytes = Encoding.UTF8.GetBytes(jsonPayload);

                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ServerUrl);
                    request.Method = "POST";
                    request.ContentType = "application/json";
                    request.ContentLength = postBytes.Length;
                    request.Timeout = 35000;

                    using (Stream requestStream = request.GetRequestStream())
                    {
                        requestStream.Write(postBytes, 0, postBytes.Length);
                    }

                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        string responseString = reader.ReadToEnd();
                        return ParseResponse(responseString);
                    }
                }
                catch (WebException wex)
                {
                    if (wex.Response is HttpWebResponse errorResponse)
                    {
                        return "[Server Code " + (int)errorResponse.StatusCode + "]: Model busy or compiling shaders. Try again in a second.";
                    }
                    return "[Offline Error]: Could not reach local AI server. Make sure model is active in Models tab.";
                }
                catch (Exception ex)
                {
                    return "[System Error]: " + ex.Message;
                }
            });
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
        }

        private static string ParseResponse(string json)
        {
            string key = "\"content\":";
            int keyIndex = json.IndexOf(key);
            if (keyIndex == -1) return "Error parsing AI response payload.";

            int start = json.IndexOf("\"", keyIndex + key.Length);
            if (start == -1) return "Error parsing opening quote.";
            start++;

            StringBuilder sb = new StringBuilder();
            bool isEscaped = false;

            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (isEscaped)
                {
                    if (c == 'n') sb.Append('\n');
                    else if (c == 'r') { }
                    else if (c == 't') sb.Append('\t');
                    else sb.Append(c);
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    isEscaped = true;
                }
                else if (c == '"')
                {
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }

    // =========================================================================
    // 7. COMPREHENSIVE TELEMETRY, RESOURCES & TARGET HARVESTER
    // =========================================================================
    public static class TelemetryHarvester
    {
        public static string GetActiveVesselTelemetry()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) return "";

            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "Flight Scene: No active vessel.";

            string localizedVesselName = Localizer.Format(v.vesselName);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Active Vessel Telemetry:");
            sb.AppendLine("• Vessel Name: " + localizedVesselName);
            sb.AppendLine("• Current Body: " + v.mainBody.bodyName);
            sb.AppendLine("• Flight Situation: " + v.situation);
            sb.AppendLine(string.Format("• Speed Readouts (Navball): Orbital {0:F1} m/s | Surface {1:F1} m/s | Vertical {2:F1} m/s", v.obt_speed, v.srfSpeed, v.verticalSpeed));
            sb.AppendLine(string.Format("• Altitude: ASL {0:F0} m | Radar {1:F0} m", v.altitude, v.radarAltitude));
            sb.AppendLine(string.Format("• G-Force: {0:F2} g", v.geeForce_immediate));

            if (v.mainBody.atmosphere)
            {
                sb.AppendLine(string.Format("• Dynamic Pressure (Q): {0:F2} kPa | Air Density {1:F4} kg/m³", v.dynamicPressurekPa, v.atmDensity));
            }

            if (v.orbit != null)
            {
                sb.AppendLine(string.Format("• Orbit: Apoapsis {0:F0} m | Periapsis {1:F0} m | Inclination {2:F2}° | Period {3:F0} s", v.orbit.ApA, v.orbit.PeA, v.orbit.inclination, v.orbit.period));
            }

            if (v.VesselDeltaV != null)
            {
                sb.AppendLine(string.Format("• Total Remaining Delta-V: {0:F0} m/s", v.VesselDeltaV.TotalDeltaVActual));
                int stage = v.currentStage;
                var stageInfo = v.VesselDeltaV.GetStage(stage);
                if (stageInfo != null)
                {
                    sb.AppendLine(string.Format("• Current Active Stage ({0}): {1:F0} m/s dV | TWR {2:F2}", stage, stageInfo.deltaVActual, stageInfo.TWRActual));
                }
            }

            sb.AppendLine(GetResourceSummary(v));

            if (FlightGlobals.fetch != null && FlightGlobals.fetch.VesselTarget != null)
            {
                ITargetable target = FlightGlobals.fetch.VesselTarget;
                double distance = Vector3d.Distance(v.GetWorldPos3D(), target.GetTransform().position);
                Vector3d relVelocity = v.GetObtVelocity() - target.GetObtVelocity();
                sb.AppendLine(string.Format("• Target: '{0}' | Distance: {1:F0} m | Relative Speed: {2:F1} m/s", target.GetName(), distance, relVelocity.magnitude));
            }

            double maxTempRatio = GetHottestPartRatio(v);
            if (maxTempRatio > 0.65)
            {
                sb.AppendLine(string.Format("• Thermal Warning: Hottest part at {0:P0} of structural limit!", maxTempRatio));
            }

            return sb.ToString();
        }

        private static string GetResourceSummary(Vessel v)
        {
            double ecCurrent = 0, ecMax = 0;
            double lfCurrent = 0, lfMax = 0;
            double oxCurrent = 0, oxMax = 0;
            double mpCurrent = 0, mpMax = 0;

            foreach (var part in v.parts)
            {
                foreach (var res in part.Resources)
                {
                    if (res.resourceName == "ElectricCharge") { ecCurrent += res.amount; ecMax += res.maxAmount; }
                    else if (res.resourceName == "LiquidFuel") { lfCurrent += res.amount; lfMax += res.maxAmount; }
                    else if (res.resourceName == "Oxidizer") { oxCurrent += res.amount; oxMax += res.maxAmount; }
                    else if (res.resourceName == "MonoPropellant") { mpCurrent += res.amount; mpMax += res.maxAmount; }
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(string.Format("• Propellant Levels: EC {0:F0}/{1:F0}", ecCurrent, ecMax));
            if (ecMax > 0 && (ecCurrent / ecMax) < 0.1) sb.Append(" [CRITICAL LOW EC! REACTION WHEELS MAY DIE!]");
            if (lfMax > 0) sb.Append(string.Format(" | LiquidFuel {0:F0}", lfCurrent));
            if (oxMax > 0) sb.Append(string.Format(" | Oxidizer {0:F0}", oxCurrent));
            if (mpMax > 0) sb.Append(string.Format(" | MonoProp {0:F0}", mpCurrent));

            return sb.ToString();
        }

        private static double GetHottestPartRatio(Vessel v)
        {
            double highest = 0;
            foreach (var part in v.parts)
            {
                if (part.maxTemp > 0)
                {
                    double ratio = part.temperature / part.maxTemp;
                    if (ratio > highest) highest = ratio;
                }
            }
            return highest;
        }
    }

    // =========================================================================
    // 8. DYNAMIC CELESTIAL ENGINE, SCENE CONTEXT & CONTRACTS HARVESTER
    // =========================================================================
    public static class KSPKnowledgeBase
    {
        public static string GetDynamicActiveBodyData()
        {
            CelestialBody body = null;

            if (HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ActiveVessel != null)
                body = FlightGlobals.ActiveVessel.mainBody;
            else if (FlightGlobals.currentMainBody != null)
                body = FlightGlobals.currentMainBody;

            if (body == null) return "Current Body: None active.";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Active Celestial Body Environment:");
            sb.AppendLine(string.Format("• Name: {0} (Orbits: {1})", body.bodyName, body.referenceBody != null ? body.referenceBody.bodyName : "Star"));
            sb.AppendLine(string.Format("• Surface Gravity: {0:F2} m/s² ({1:F2}g)", body.GeeASL * 9.81f, body.GeeASL));
            sb.AppendLine(string.Format("• Radius: {0:F0} km | Escape Velocity: {1:F0} m/s", body.Radius / 1000f, Math.Sqrt(2 * body.gravParameter / body.Radius)));

            if (body.atmosphere)
            {
                sb.AppendLine(string.Format("• Atmosphere: YES (Depth: {0:F0}m, Sea Level Pressure: {1:F2} kPa, Oxygen: {2})", 
                    body.atmosphereDepth, body.atmospherePressureSeaLevel, body.atmosphereContainsOxygen ? "YES" : "NO"));
            }
            else
            {
                sb.AppendLine("• Atmosphere: NO (Vacuum - parachutes will NOT work)");
            }

            return sb.ToString();
        }

        public static string GetCurrentSceneContext()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Current Scene in KSP: " + HighLogic.LoadedScene.ToString());

            if (HighLogic.LoadedScene == GameScenes.EDITOR)
            {
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                {
                    string craftName = Localizer.Format(EditorLogic.fetch.ship.shipName);
                    sb.AppendLine(string.Format("• VAB/SPH Craft: '{0}' (Parts: {1}, Mass: {2:F2}t)", 
                        craftName, EditorLogic.fetch.ship.parts.Count, EditorLogic.fetch.ship.GetTotalMass()));
                }
                else
                {
                    sb.AppendLine("• VAB/SPH Editor: Designing new vessel.");
                }
            }
            else if (HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                if (Funding.Instance != null)
                    sb.AppendLine(string.Format("• Career Status: {0:F0} Funds | {1:F0} Science", Funding.Instance.Funds, ResearchAndDevelopment.Instance != null ? ResearchAndDevelopment.Instance.Science : 0));
            }

            return sb.ToString();
        }

        public static string GetActiveContractsSummary()
        {
            if (ContractSystem.Instance == null || ContractSystem.Instance.Contracts == null) return "";

            StringBuilder sb = new StringBuilder();
            bool hasContracts = false;

            foreach (var contract in ContractSystem.Instance.Contracts)
            {
                if (contract.ContractState == Contract.State.Active)
                {
                    if (!hasContracts)
                    {
                        sb.AppendLine("Active Career Contracts:");
                        hasContracts = true;
                    }
                    sb.AppendLine(string.Format("• {0} (Reward: {1:F0} Funds)", contract.Title, contract.FundsCompletion));
                }
            }

            return hasContracts ? sb.ToString() : "";
        }
    }
}