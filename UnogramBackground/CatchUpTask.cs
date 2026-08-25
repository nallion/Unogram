// UnogramBackground/CatchUpTask.cs
// Фоновая задача, запускается по TimeTrigger раз в 15 минут.
// Отдельный процесс: XAML-приложение не загружается, поэтому весь бюджет
// памяти фоновой задачи достаётся TDLib, а не оболочке.
// Самодостаточна — ничего не линкуется из основного проекта.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace UnogramBackground
{
    public sealed class CatchUpTask : IBackgroundTask
    {
        // ------------------------------------------------------------------
        // TDLib
        // ------------------------------------------------------------------

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr td_json_client_create();

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void td_json_client_send(IntPtr client, IntPtr request);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr td_json_client_receive(IntPtr client, double timeout);

        [DllImport("tdjson.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void td_json_client_destroy(IntPtr client);

        private static void Send(IntPtr client, string request)
        {
            if (string.IsNullOrEmpty(request)) return;
            byte[] bytes = Encoding.UTF8.GetBytes(request + "\0");
            IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                td_json_client_send(client, ptr);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        private static string ReadUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return null;
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            if (len == 0) return string.Empty;
            byte[] buffer = new byte[len];
            Marshal.Copy(ptr, buffer, 0, len);
            return Encoding.UTF8.GetString(buffer);
        }

        // ------------------------------------------------------------------
        // Константы, общие с приложением
        // ------------------------------------------------------------------

        // Приложение держит этот мьютекс, пока открыт его клиент TDLib.
        // Значение продублировано намеренно: компонент самодостаточен, а WinRT
        // не позволяет публичные константы. Должно совпадать с
        // BackgroundService.TdSessionMutexName в основном проекте.
        private const string TdSessionMutexName = "Unogram.TdSession";

        private const string LogFolderName = "Unogram"; // тут же лежит база TDLib (td_db), не только логи
        private const string ToastGroup = "unogram";

        private const int SessionBudgetSeconds = 40;
        private const int MaxMessageAgeSeconds = 3600;

        private BackgroundTaskDeferral _deferral;
        private volatile bool _cancelled;

        // ------------------------------------------------------------------

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            taskInstance.Canceled += (s, reason) =>
            {
                _cancelled = true;
                Diag("Task cancelled: " + reason);
            };

            try
            {
                LogMemory("task start");

                // Приложение открыто — база занята им, отходим в сторону.
                Mutex mutex = null;
                bool held = false;
                try
                {
                    mutex = new Mutex(false, TdSessionMutexName);
                    try { held = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { held = true; }  // владелец умер

                    if (!held)
                    {
                        Diag("Skipped: app holds the TDLib session");
                        return;
                    }

                    await RunSessionAsync();
                }
                finally
                {
                    if (mutex != null)
                    {
                        if (held) { try { mutex.ReleaseMutex(); } catch { } }
                        mutex.Dispose();
                    }
                }

                LogMemory("task end");
            }
            catch (Exception ex)
            {
                Diag("Task failed: " + ex.Message);
            }
            finally
            {
                _deferral.Complete();
            }
        }

        private async Task RunSessionAsync()
        {
            IntPtr client = IntPtr.Zero;
            int notified = 0;
            bool authorized = false;
            string exitReason = "budget exhausted";

            try
            {
                var appFolder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync(LogFolderName, CreationCollisionOption.OpenIfExists);
                string dbPath = appFolder.Path.Replace("\\", "/") + "/td_db";
                var filesFolder = await appFolder.CreateFolderAsync("td_db_files",
                    CreationCollisionOption.OpenIfExists);

                client = td_json_client_create();
                if (client == IntPtr.Zero) { Diag("client create failed"); return; }
                LogMemory("tdjson loaded");

                var parameters = new JObject {
                    ["@type"] = "setTdlibParameters",
                    ["use_test_dc"] = false,
                    ["database_directory"] = dbPath,
                    ["files_directory"] = filesFolder.Path.Replace("\\", "/"),
                    ["database_encryption_key"] = "",
                    ["use_file_database"] = true,
                    ["use_chat_info_database"] = true,
                    ["use_message_database"] = true,
                    ["use_secret_chats"] = false,
                    ["api_id"] = Secrets.ApiId,
                    ["api_hash"] = Secrets.ApiHash,
                    ["system_language_code"] = "ru",
                    ["device_model"] = "Lumia",
                    ["system_version"] = "10",
                    ["application_version"] = "1.2"
                };

                IntPtr c = client;
                var deadline = DateTime.UtcNow.AddSeconds(SessionBudgetSeconds);
                var titles = new Dictionary<long, string>();
                var mutedChats = new HashSet<long>(); // чаты, замьюченные на сервере Telegram (mute_for > 0)
                var seen = new HashSet<long>();
                bool abort = false;

                await Task.Run(() =>
                {
                    while (true)
                    {
                        if (DateTime.UtcNow >= deadline) { exitReason = "budget exhausted"; break; }
                        if (_cancelled) { exitReason = "task cancelled"; break; }
                        if (abort) { exitReason = "not signed in"; break; }

                        IntPtr res = td_json_client_receive(c, 1.0);
                        if (res == IntPtr.Zero) continue;
                        string json = ReadUtf8(res);
                        if (string.IsNullOrEmpty(json)) continue;

                        JObject u;
                        try { u = JObject.Parse(json); } catch { continue; }
                        string type = u["@type"]?.ToString();

                        if (type == "error")
                        {
                            Diag("TDLib error: " + (json.Length > 200 ? json.Substring(0, 200) : json));
                            continue;
                        }

                        if (type == "updateAuthorizationState")
                        {
                            string state = u["authorization_state"]?["@type"]?.ToString();
                            Diag("state: " + state);
                            if (state == "authorizationStateWaitTdlibParameters")
                                Send(c, parameters.ToString(Newtonsoft.Json.Formatting.None));
                            else if (state == "authorizationStateReady")
                            {
                                if (!authorized) LogMemory("tdlib ready");
                                authorized = true;
                                // Без явного запроса TDLib не обязан присылать updateNewChat
                                // для всех чатов сразу — а без него mutedChats/titles не
                                // успевают наполниться раньше, чем придут updateNewMessage
                                // по этим же чатам, и фильтр по мьюту просто не срабатывает.
                                Send(c, "{\"@type\":\"loadChats\",\"chat_list\":{\"@type\":\"chatListMain\"},\"limit\":200}");
                            }
                            else if (state != null && state.StartsWith("authorizationStateWait"))
                                abort = true;
                        }
                        else if (type == "updateNewChat")
                        {
                            long cid = u["chat"]?["id"]?.ToObject<long>() ?? 0;
                            string t = u["chat"]?["title"]?.ToString();
                            if (cid != 0 && !string.IsNullOrEmpty(t)) titles[cid] = t;
                            // Личка, группы и каналы — все уведомляют одинаково, если
                            // чат не замьючен на сервере Telegram (см. mute_for ниже).
                            int muteFor = u["chat"]?["notification_settings"]?["mute_for"]?.ToObject<int>() ?? 0;
                            if (cid != 0)
                            {
                                if (muteFor > 0) mutedChats.Add(cid);
                                else mutedChats.Remove(cid);
                            }
                        }
                        else if (type == "updateChatNotificationSettings")
                        {
                            // Настройки могли поменять с другого устройства, пока эта
                            // задача не запускалась — статус мьюта тот же, что и на сервере.
                            long uncsId = u["chat_id"]?.ToObject<long>() ?? 0;
                            int uncsMuteFor = u["notification_settings"]?["mute_for"]?.ToObject<int>() ?? 0;
                            if (uncsId != 0)
                            {
                                if (uncsMuteFor > 0) mutedChats.Add(uncsId);
                                else mutedChats.Remove(uncsId);
                            }
                        }
                        else if (type == "updateNewMessage" && authorized)
                        {
                            var m = u["message"];
                            if (m == null) continue;
                            if (m["is_outgoing"]?.ToObject<bool>() ?? false) continue;

                            long chatId = m["chat_id"]?.ToObject<long>() ?? 0;
                            if (mutedChats.Contains(chatId)) continue; // чат замьючен на сервере — пропускаем

                            long mid = m["id"]?.ToObject<long>() ?? 0;
                            if (mid != 0 && !seen.Add(mid)) continue;

                            long sentAt = m["date"]?.ToObject<long>() ?? 0;
                            if (sentAt > 0 &&
                                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAt > MaxMessageAgeSeconds)
                                continue;

                            if (!IsNewerThanLastNotified(chatId, mid)) continue;

                            string title = titles.ContainsKey(chatId) ? titles[chatId] : "Unogram";
                            string body = Describe(m["content"]);
                            ShowToast(title, body, chatId);
                            AddTilePreview(chatId, title, body);
                            RememberLastNotified(chatId, mid);
                            notified++;
                        }
                    }
                });

                Diag("session ended: reason=" + exitReason
                     + ", authorized=" + authorized + ", toasts=" + notified);

                CloseClient(c);
            }
            catch (Exception ex)
            {
                Diag("session failed: " + ex.Message);
            }
        }

        /// <summary>close -> ждём authorizationStateClosed -> destroy. Иначе база остаётся грязной.</summary>
        private void CloseClient(IntPtr client)
        {
            try
            {
                Send(client, "{\"@type\":\"close\"}");
                var stop = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < stop)
                {
                    IntPtr res = td_json_client_receive(client, 0.5);
                    if (res == IntPtr.Zero) continue;
                    string json = ReadUtf8(res);
                    if (!string.IsNullOrEmpty(json) && json.Contains("authorizationStateClosed")) break;
                }
                td_json_client_destroy(client);
            }
            catch (Exception ex) { Diag("close failed: " + ex.Message); }
        }

        /// <summary>
        /// Проект намеренно не линкует ничего из основного (см. шапку файла),
        /// поэтому Loc.cs сюда не подключаем — вместо этого небольшой
        /// самодостаточный словарь на те же 4 языка, читающий тот же ключ
        /// настроек ("ui_language"), что и основное приложение, так что смена
        /// языка в приложении сразу отражается и в фоновых уведомлениях.
        /// </summary>
        private static string BgLang()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                if (v.ContainsKey("ui_language"))
                {
                    string lang = v["ui_language"] as string;
                    if (lang == "ru" || lang == "uk" || lang == "he") return lang;
                }
            }
            catch { }
            return "en";
        }

        private static string BgT(string en, string ru, string uk, string he)
        {
            switch (BgLang())
            {
                case "ru": return ru;
                case "uk": return uk;
                case "he": return he;
                default:   return en;
            }
        }

        private static string Describe(JToken content)
        {
            switch (content?["@type"]?.ToString() ?? "")
            {
                case "messageText":      return content["text"]?["text"]?.ToString() ?? "";
                case "messagePhoto":     return BgT("Photo", "Фото", "Фото", "תמונה");
                case "messageVideo":     return BgT("Video", "Видео", "Відео", "וידאו");
                case "messageVoiceNote": return BgT("Voice message", "Голосовое сообщение", "Голосове повідомлення", "הודעה קולית");
                case "messageVideoNote": return BgT("Video message", "Видеосообщение", "Відеоповідомлення", "הודעת וידאו");
                case "messageSticker":   return BgT("Sticker", "Стикер", "Стікер", "מדבקה");
                case "messageDocument":  return BgT("File", "Файл", "Файл", "קובץ");
                case "messageAnimation": return "GIF";
                default:                 return BgT("New message", "Новое сообщение", "Нове повідомлення", "הודעה חדשה");
            }
        }

        private static void ShowToast(string title, string body, long chatId)
        {
            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(string.IsNullOrEmpty(title) ? "Unogram" : title));
                texts[1].AppendChild(xml.CreateTextNode(body ?? ""));
                var toast = new ToastNotification(xml);
                toast.Tag = "c" + (chatId < 0 ? "n" : "") + Math.Abs(chatId).ToString();
                toast.Group = ToastGroup;
                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch { }
        }

        // ================================================================
        // Живая плитка — общий ключ LocalSettings с MainPage.xaml.cs (см.
        // TilePreviewsKey там же). Это два независимых процесса без общего
        // in-memory состояния, поэтому синхронизация только через диск.
        // Чат тут никогда не "открывают" — только добавление, чистит
        // накопленное только основное приложение при заходе в чат.
        // ================================================================
        private const string TilePreviewsKey = "tile_previews";
        private const int MaxTilePreviews = 5;

        private static void AddTilePreview(long chatId, string sender, string text)
        {
            try
            {
                var list = LoadTilePreviews();
                list.Insert(0, new JObject { ["c"] = chatId, ["s"] = sender ?? "", ["t"] = text ?? "" });
                if (list.Count > MaxTilePreviews) list.RemoveRange(MaxTilePreviews, list.Count - MaxTilePreviews);
                var arr = new JArray();
                foreach (var o in list) arr.Add(o);
                ApplicationData.Current.LocalSettings.Values[TilePreviewsKey] = arr.ToString(Newtonsoft.Json.Formatting.None);
                ApplyTileXml(list);
            }
            catch { }
        }

        private static List<JObject> LoadTilePreviews()
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                if (!v.ContainsKey(TilePreviewsKey)) return new List<JObject>();
                var arr = JArray.Parse((string)v[TilePreviewsKey]);
                var result = new List<JObject>();
                foreach (var t in arr) if (t is JObject jo) result.Add(jo);
                return result;
            }
            catch { return new List<JObject>(); }
        }

        private static void ApplyTileXml(List<JObject> list)
        {
            try
            {
                if (list.Count == 0)
                {
                    TileUpdateManager.CreateTileUpdaterForApplication().Clear();
                    return;
                }
                string Trim(string s, int maxLen) =>
                    string.IsNullOrEmpty(s) ? "" : (s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s);
                string Esc(string s) =>
                    string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
                string Line(JObject p, int sLen, int tLen) =>
                    Esc(Trim(p["s"]?.ToString(), sLen) + ": " + Trim(p["t"]?.ToString(), tLen));

                var sb = new StringBuilder();
                sb.Append("<tile><visual version=\"2\">");

                // Small — как и в MainPage.xaml.cs: без этого binding'а плитка,
                // закреплённая в размере Small, никогда ничего "живого" не покажет.
                sb.Append("<binding template=\"TileSmall\" branding=\"none\">");
                sb.Append("<text hint-style=\"base\" hint-align=\"center\">").Append(Esc(Trim(list[0]["s"]?.ToString(), 12))).Append("</text>");
                sb.Append("</binding>");

                sb.Append("<binding template=\"TileMedium\" branding=\"nameAndLogo\">");
                for (int i = 0; i < Math.Min(2, list.Count); i++)
                {
                    sb.Append("<text hint-style=\"captionSubtle\">").Append(Esc(Trim(list[i]["s"]?.ToString(), 20))).Append("</text>");
                    sb.Append("<text hint-style=\"caption\">").Append(Esc(Trim(list[i]["t"]?.ToString(), 40))).Append("</text>");
                }
                sb.Append("</binding>");

                // Lock screen detailed status. The user picks one app in lock screen
                // settings; its text comes from the WIDE binding only - values on small,
                // medium or large are ignored. Two documented forms exist and both are
                // emitted here: hint-lockDetailedStatus1..3 on the binding element, and
                // id="1".."3" on text elements that are immediate children of it.
                sb.Append("<binding template=\"TileWide\" branding=\"nameAndLogo\"");
                for (int i = 0; i < Math.Min(3, list.Count); i++)
                    sb.Append(" hint-lockDetailedStatus").Append(i + 1).Append("=\"")
                      .Append(Line(list[i], 30, 60)).Append("\"");
                sb.Append(">");
                // id="1".."3" on the first three lines: the documented alternative to the
                // hint-* attributes above, and the one Microsoft calls preferred. The text
                // elements must be immediate children of the wide binding, which they are.
                // Both forms are emitted because the hints alone did not render on 15254.
                for (int i = 0; i < list.Count; i++) {
                    sb.Append("<text");
                    if (i < 3) sb.Append(" id=\"").Append(i + 1).Append("\"");
                    sb.Append(" hint-style=\"captionSubtle\">").Append(Line(list[i], 30, 60)).Append("</text>");
                }
                sb.Append("</binding>");

                sb.Append("<binding template=\"TileLarge\" branding=\"nameAndLogo\">");
                foreach (var p in list) sb.Append("<text hint-style=\"body\">").Append(Line(p, 30, 80)).Append("</text>");
                sb.Append("</binding>");

                sb.Append("</visual></tile>");

                var tileXml = new XmlDocument();
                tileXml.LoadXml(sb.ToString());
                TileUpdateManager.CreateTileUpdaterForApplication().Update(new TileNotification(tileXml));
            }
            catch { }
        }

        private static bool IsNewerThanLastNotified(long chatId, long messageId)
        {
            try
            {
                var v = ApplicationData.Current.LocalSettings.Values;
                string key = "notified_" + chatId;
                if (!v.ContainsKey(key)) return true;
                return messageId > Convert.ToInt64(v[key]);
            }
            catch { return true; }
        }

        private static void RememberLastNotified(long chatId, long messageId)
        {
            try { ApplicationData.Current.LocalSettings.Values["notified_" + chatId] = messageId; }
            catch { }
        }

        private static void LogMemory(string stage)
        {
            try
            {
                ulong limit = Windows.System.MemoryManager.AppMemoryUsageLimit;
                ulong used = Windows.System.MemoryManager.AppMemoryUsage;
                Diag(string.Format("Memory ({0}): limit={1} KB, used={2} KB, free={3} KB",
                    stage, limit / 1024, used / 1024, limit > used ? (limit - used) / 1024 : 0));
            }
            catch { }
        }

        // Запись диагностики на диск отключена — приложение ничего не должно
        // писать в файлы/настройки для логов. Debug.WriteLine никуда не
        // сохраняется (только в отладчик), поэтому оставлен как есть.
        private static void Diag(string message)
        {
            System.Diagnostics.Debug.WriteLine("[BGTASK] " + message);
        }
    }
}
