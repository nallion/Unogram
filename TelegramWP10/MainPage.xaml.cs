using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Newtonsoft.Json.Linq;

namespace TelegramWP10
{
    public sealed partial class MainPage : Page
    {
        private IntPtr _client;

        private CallsService _calls;

        /// <summary>
        /// Voice calls. Created lazily and given a send delegate rather than the
        /// handle itself, so it always reads the current _client - the pointer is
        /// zeroed on shutdown and must not be captured by value.
        /// </summary>
        private CallsService Calls {
            get {
                if (_calls == null) {
                    _calls = new CallsService(json => TdJson.SendUtf8(_client, json), Log);
                    _calls.CallChanged += OnCallChanged;
                    _calls.ControllerStateChanged += OnCallControllerStateChanged;
                }
                return _calls;
            }
        }
        // ---- Back-button exit from the main screen ----
        // The LongPolling thread reads these flags, hence volatile: it has to
        // see the close immediately, not whenever the JIT re-reads the field.
        private volatile bool _shuttingDown = false;
        private volatile bool _tdClosing = false;
        private DateTime _tdCloseDeadline = DateTime.MaxValue;
        /// <summary>Set by the LongPolling thread once it has left the read loop.</summary>
        private readonly TaskCompletionSource<bool> _pollingStopped = new TaskCompletionSource<bool>();
        private ObservableCollection<ChatItem> _chatListItems = new ObservableCollection<ChatItem>();
        private List<ChatItem> _allChatItems = new List<ChatItem>(); // все чаты для фильтрации
        private int _currentFolderId = -1;
        private Dictionary<int, List<long>> _folderChatIds = new Dictionary<int, List<long>>();
        private int _pendingFolderLoad = 0;
        private Queue<int> _folderLoadQueue = new Queue<int>();

        private void LoadNextFolder() {
            if (_folderLoadQueue.Count == 0 || _pendingFolderLoad != 0) return;
            _pendingFolderLoad = _folderLoadQueue.Dequeue();
            TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListFolder\",\"chat_folder_id\":" + _pendingFolderLoad + "},\"limit\":100}");
        }
        private ObservableCollection<MessageItem> _messageItems = new ObservableCollection<MessageItem>();
        private Dictionary<long, ChatItem> _chatsDict = new Dictionary<long, ChatItem>();
        private Dictionary<long, JToken> _rawChatsDict = new Dictionary<long, JToken>(); // сырой JSON чата
        private Dictionary<long, JToken> _usersDict = new Dictionary<long, JToken>();
        private Dictionary<long, JToken> _supergroupDict = new Dictionary<long, JToken>();
        private Dictionary<long, long> _fileToChatId = new Dictionary<long, long>();
        private Dictionary<long, long> _inlinePhotoFileId = new Dictionary<long, long>(); // msgId → file_id уменьшенного превью (не оригинала)
        private HashSet<long> _stickerVideoFileIds = new HashSet<long>(); // file_id миниатюр видео-стикеров (thumbnailFormatMpeg4) — не картинка, а mp4-клип
        private Dictionary<long, long> _fileToSenderUserId = new Dictionary<long, long>();  // file_id → userId, для аватарок отправителей в группах
        private Dictionary<long, BitmapImage> _senderAvatarCache = new Dictionary<long, BitmapImage>(); // userId → уже загруженная аватарка (на всю сессию, не только текущий чат)
        private HashSet<long> _senderAvatarRequested = new HashSet<long>(); // userId, для которых уже запрошена загрузка — не дублируем
        private Dictionary<long, SearchResultItem> _fileToSearchResult = new Dictionary<long, SearchResultItem>();
        private Dictionary<long, bool> _pendingPinnedPositions = new Dictionary<long, bool>();
        private Dictionary<long, long> _uploadFileToMsgId = new Dictionary<long, long>(); // remote file_id → msgId для прогресса upload // chatId → isPinned до updateNewChat
        private Dictionary<long, long> _fileToMsgId = new Dictionary<long, long>();
        private Dictionary<string, long> _remoteUniqueIdToMsgId = new Dictionary<string, long>(); // remote.unique_id → msgId
        private Dictionary<long, long> _videoFileIds = new Dictionary<long, long>(); // file_id → msgId только для видеофайлов
        private Dictionary<long, long> _audioFileIds = new Dictionary<long, long>(); // file_id → msgId только для аудиофайлов (для загрузки по клику)
        private Dictionary<long, MessageItem> _messagesDict = new Dictionary<long, MessageItem>();
        // replyMsgId → MessageItem которому нужно заполнить ReplyToText
        private Dictionary<long, MessageItem> _replyRequests = new Dictionary<long, MessageItem>();
        private long _currentChatId = 0;
        private System.Threading.Mutex _tdSessionMutex = null;
        private long _myUserId = 0;
        private bool _waitingForMe = false;
        private bool _contactsPendingMyId = false;
        private long _fullPhotoMsgId = 0;
        private string _currentPhotoOverlayPath = null;   // путь к уже докачанному фото в оверлее — для кнопки "Сохранить"
        private bool _pendingPhotoSave = false;            // нажали "Сохранить" до того, как докачался полный размер
        private HashSet<long> _pendingSaveMsgIds = new HashSet<long>(); // видео/аудио, которые нужно сохранить сразу после докачки
        private HashSet<long> _editRefreshPendingIds = new HashSet<long>(); // id, для которых getMessage запрошен именно из-за updateMessageEdited

        // ======= Поиск внутри текущей переписки =======
        private bool _chatSearchAwaitingResults = false; // ждём ответ именно на searchChatMessages, не на getChatHistory
        private string _chatSearchQuery = "";
        private List<long> _chatSearchResultIds = new List<long>();
        private int _chatSearchResultIndex = -1;
        private ObservableCollection<SearchResultItem> _chatSearchResultItems = new ObservableCollection<SearchResultItem>();
        private Windows.UI.Xaml.DispatcherTimer _chatSearchDebounceTimer;
        private TaskCompletionSource<bool> _optimizeStorageTcs = null; // ждём storageStatistics — подтверждение, что чистка кэша реально завершена
        private long _threadMessageId = 0;
        private long _threadChatId = 0;
        private bool _currentChatIsGroup = false;
        private bool _currentChatIsChannel = false;
        private Windows.UI.Xaml.DispatcherTimer _statusTimer;
        private Windows.UI.Xaml.DispatcherTimer _audioPositionTimer;
        private Windows.UI.Xaml.DispatcherTimer _typingTimer;
        private bool _audioSliderDragging = false;
        private long _pendingHistoryChatId = 0;
        private int _historyRetryCount = 0;
        private bool _loadingOlderHistory = false;
        private bool _hasMoreHistory = true;
        private bool _trimming = false;
        private Windows.UI.Xaml.DispatcherTimer _restoreTimer = null;
        private ItemsStackPanel _messagesStackPanel = null;

        private void SetScrollMode(ItemsUpdatingScrollMode mode) {
            if (_messagesStackPanel == null)
                _messagesStackPanel = FindVisualChild<ItemsStackPanel>(MessagesListView);
            if (_messagesStackPanel != null)
                _messagesStackPanel.ItemsUpdatingScrollMode = mode;
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject {
            int n = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++) {
                var c = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (c is T t) return t;
                var r = FindVisualChild<T>(c);
                if (r != null) return r;
            }
            return null;
        }
        private Windows.UI.Xaml.DispatcherTimer _scrollTimer;
        private bool _autoScrolling = false;
        private long _pendingStickerFileId = 0;
        private long _pendingStickerChatId = 0;
        // uploadFile pending
        private string _pendingUploadType = ""; // "doc" или "voice"
        private long   _pendingUploadChatId = 0; // true пока идёт автоскролл вниз после загрузки
        private long _currentChatOutboxReadId = 0;
        private long _lastReadInboxMsgId = 0;
        private long _pinnedMessageId = 0;
        private long _pendingPinnedChatId = 0;
        private bool _currentChatIsBot = false;
        private long _pendingScrollToMsgId = 0; // скролл к сообщению после открытия чата
        private Dictionary<long, long> _pinnedTextRequests = new Dictionary<long, long>(); // pinnedMsgId → serviceMsgId
        private bool _loadingChats = false;
        private bool _mainListLoaded = false; // основной список полностью загружен
        private Queue<long> _pendingChatIds = new Queue<long>();
        private string _dbPath = "";
        private bool _connectionReady = false;
        private bool _isAuthorized = false;
        private bool _isLoadingHistory = false;

        // MTProxy
        private class ProxyEntry { public string Host; public int Port; public string Secret; }
        private List<ProxyEntry> _proxyList = new List<ProxyEntry>();
        private int _proxyIndex = 0;
        private int _currentProxyId = 0;
        private Windows.UI.Xaml.DispatcherTimer _proxyTimer;
        private Windows.UI.Xaml.DispatcherTimer _connectingTimer; // таймер 10с на подключение
        private bool _proxyConnected = false;
        private bool _proxyApplied = false;
        private bool _soundEnabled = false; // уведомления (звук+баннер), пока приложение открыто
        // Стикеры
        private bool _stickerPanelOpen = false;
        private List<ContactItem> _contactItems = new List<ContactItem>();
        private long _pendingContactUserId = 0; // userId ожидающий createPrivateChat
        private List<StickerItem> _currentStickerItems = new List<StickerItem>();
        private Dictionary<long, long> _stickerThumbToItem = new Dictionary<long, long>(); // thumbFileId → fileId
        private List<long> _loadedStickerSetIds = new List<long>(); // чтобы не применять дважды

        // Режим прокси
        private enum ProxyMode { None, Auto, Mtproto, Http, Socks }
        private ProxyMode _proxyMode = ProxyMode.None;
        private bool _isLightTheme = false;
        // Цвета пузырей — обновляются при смене темы
        internal static string BubbleColorOut = "#0088cc";
        internal static string BubbleColorIn  = "#333333"; // по умолчанию — прямое подключение
        private bool _isRecording = false;
        private Windows.Media.Capture.MediaCapture _mediaCapture = null;
        private Windows.Storage.StorageFile _recordingFile = null;
        private Windows.Media.Capture.MediaCapture _videoCaptureCapture = null;
        private Windows.Storage.StorageFile _videoNoteFile = null;
        private Windows.UI.Xaml.DispatcherTimer _videoNoteTimer = null;
        private int _videoNoteSeconds = 0;
        private const int MaxVideoNoteSeconds = 60;
        private Windows.Media.Playback.MediaPlayer _currentAudioPlayer = null;
        private long _currentAudioMsgId = 0;
        private Windows.Media.Core.MediaSource _currentAudioSource = null;
        private TimeSpan _currentAudioPosition = TimeSpan.Zero;
        private string _currentAudioFilePath = null;
        private bool _currentAudioIsBass = false; // true — играем через BassPlayer (.oga/.ogg), а не MediaPlayer
        private Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession _mediaSession = null;
        private long _pendingDeleteChatId = 0;
        private StorageFolder _filesFolder = null;
        private ObservableCollection<ChatItem> _archiveChatItems = new ObservableCollection<ChatItem>();
        private bool _inArchive = false;
        private bool _archiveLoaded = false;
        private bool _loadingArchive = false;
        private bool _loadingArchiveIds = false;   // pre-fetch id архива до загрузки главного
        private HashSet<long> _archiveChatIds = new HashSet<long>(); // id чатов архива
        private HashSet<long> _pendingGetChat = new HashSet<long>(); // id запрошенных через getChat из LoadNextChat

        private Windows.UI.ViewManagement.InputPane _inputPane;

        /// <summary>The live page, so App can route a toast action to it.</summary>
        internal static MainPage Current;

        /// <summary>
        /// Call already accepted from the toast. TDLib keeps reporting
        /// callStatePending for a moment afterwards, so without this the overlay
        /// offers an Answer button for a call that is already being answered.
        /// </summary>
        private long _answeredCallId;

        /// <summary>
        /// Acts on an Answer/Decline button from the incoming-call toast.
        /// </summary>
        internal void HandleLaunchArgument(string argument) {
            if (string.IsNullOrEmpty(argument)) return;
            Log("call: toast action " + argument);
            if (argument == BackgroundService.CallActionAnswer) {
                BackgroundService.HideCallToast();
                if (Calls.ActiveCall != null) _answeredCallId = Calls.ActiveCall.Id;
                Calls.AcceptIncomingCall();
            } else if (argument == BackgroundService.CallActionDecline) {
                BackgroundService.HideCallToast();
                Calls.HangUp();
            }
        }

        public MainPage()
        {
            Current = this;
            this.InitializeComponent();
            // Полноэкранный режим: прячет и статус-бар, и системную панель
            // навигации (назад/Windows/поиск) — они превращаются в скрытые
            // оверлеи, вызываемые свайпом от края экрана, как у видеоплееров
            // (VLC и т.п.). В обычном оконном режиме эту панель программно
            // убрать нельзя — только через TryEnterFullScreenMode().
            try {
                Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().TryEnterFullScreenMode();
            } catch { }
            // Фоновая задача теперь в отдельном процессе, поэтому база защищается
            // именованным мьютексом: задача его проверяет и уступает приложению.
            try {
                _tdSessionMutex = new System.Threading.Mutex(false, BackgroundService.TdSessionMutexName);
                try { _tdSessionMutex.WaitOne(10000); }
                catch (System.Threading.AbandonedMutexException) { }
            } catch { _tdSessionMutex = null; }
            _client = TdJson.td_json_client_create();
            ActiveClient = _client;   // видно фоновой задаче догрузки
            ChatListView.ItemsSource = _chatListItems;
            MessagesListView.ItemsSource = _messageItems;
            // Клавиатура сама не должна двигать всю страницу — иначе система
            // тащит вверх весь визуальный узел (включая шапку чата в Grid.Row="0"),
            // пытаясь показать поле ввода. Подрезаем снизу только область
            // сообщений и явно сообщаем системе, что сами держим фокус в кадре.
            _inputPane = Windows.UI.ViewManagement.InputPane.GetForCurrentView();
            _inputPane.Showing += InputPane_Showing;
            _inputPane.Hiding += InputPane_Hiding;
            // Загружаем сохранённый режим прокси до старта TDLib
            var ls = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (ls.Values.ContainsKey("proxy_mode"))
                _proxyMode = (ProxyMode)(int)ls.Values["proxy_mode"];
            // Загружаем тему
            if (ls.Values.ContainsKey("light_theme"))
                _isLightTheme = (bool)ls.Values["light_theme"];
            if (ls.Values.ContainsKey("sound_enabled"))
                _soundEnabled = (bool)ls.Values["sound_enabled"];
            // Подписка на скролл идёт через x:Name="MessagesScrollViewer" в XAML — ViewChanged там же
            this.Loaded += (s, e2) => {
                if (SoundToggleItem != null)
                    ApplyLanguage();
                    if (BackgroundService.KeepAliveEnabled) {
                        var ignoredKeepAlive = BackgroundService.Instance.StartKeepAliveAsync()
                            .ContinueWith(t => {
                                var ignoredUi = Dispatcher.RunAsync(
                                    Windows.UI.Core.CoreDispatcherPriority.Low,
                                    () => UpdateKeepAliveMenuText());
                            });
                    }
            };
            // ApplyTheme вызывается в Loaded когда все элементы готовы
            this.Loaded += (s, e) => ApplyTheme();
            // Сбрасываем UI в начальное состояние (на случай restore после suspend)
            LoginPanel.Visibility = Visibility.Visible;
            ChatListView.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Visible;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            MessagesListView.Visibility = Visibility.Collapsed;
            // Таймер обновления статуса "был(а) N мин. назад"
            _statusTimer = new Windows.UI.Xaml.DispatcherTimer();
            _statusTimer.Interval = TimeSpan.FromSeconds(60);
            _statusTimer.Tick += (s, e) => {
                if (_currentChatId != 0 && _usersDict.ContainsKey(_currentChatId))
                    UpdateChatStatus(_usersDict[_currentChatId]["status"]);
            };
            // Таймер сброса "печатает..." — 5 секунд
            _typingTimer = new Windows.UI.Xaml.DispatcherTimer();
            _typingTimer.Interval = TimeSpan.FromSeconds(7);
            _typingTimer.Tick += (s, e) => {
                _typingTimer.Stop();
                if (_currentChatId != 0 && _usersDict.ContainsKey(_currentChatId))
                    UpdateChatStatus(_usersDict[_currentChatId]["status"]);
                else if (_currentChatId != 0)
                    CurrentChatStatus.Text = "";
            };
            _statusTimer.Start();
            // Таймер обновления позиции аудио (каждые 500мс)
            _audioPositionTimer = new Windows.UI.Xaml.DispatcherTimer();
            _audioPositionTimer.Interval = TimeSpan.FromMilliseconds(500);
            _audioPositionTimer.Tick += (s, e) => {
                if (_audioSliderDragging) return;
                if (_currentAudioIsBass) {
                    if (!BassPlayer.HasActiveStream || !_messagesDict.ContainsKey(_currentAudioMsgId)) return;
                    if (BassPlayer.HasEnded()) {
                        // Доиграло само — сбрасываем состояние, как MediaEnded у MediaPlayer
                        var endedItem = _messagesDict[_currentAudioMsgId];
                        endedItem.AudioPlayStatus = "▶";
                        BassPlayer.Stop();
                        _currentAudioIsBass = false;
                        _currentAudioMsgId = 0;
                        _currentAudioFilePath = null;
                        return;
                    }
                    var bassItem = _messagesDict[_currentAudioMsgId];
                    var bassLen = BassPlayer.GetLength();
                    var bassPos = BassPlayer.GetPosition();
                    if (bassLen.TotalSeconds > 0) bassItem.AudioDurationSeconds = bassLen.TotalSeconds;
                    bassItem.AudioPosition = bassPos.TotalSeconds;
                    bassItem.AudioPositionText = $"{(int)bassPos.TotalMinutes}:{bassPos.Seconds:D2}";
                    _currentAudioPosition = bassPos;
                    return;
                }
                if (_currentAudioPlayer == null) return;
                var session = _currentAudioPlayer.PlaybackSession;
                if (session.NaturalDuration.TotalSeconds > 0 && _messagesDict.ContainsKey(_currentAudioMsgId)) {
                    var item = _messagesDict[_currentAudioMsgId];
                    item.AudioDurationSeconds = session.NaturalDuration.TotalSeconds;
                    item.AudioPosition = session.Position.TotalSeconds;
                    var pos = session.Position;
                    item.AudioPositionText = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2}";
                    _currentAudioPosition = session.Position; // сохраняем для восстановления после resume
                }
            };
            _audioPositionTimer.Start();
            // Системная кнопка "назад"
            var sysNav = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            sysNav.BackRequested += (s, e) => HandleSystemBackRequest(e);
            InitAsync();
            // Логируем lifecycle приложения для диагностики фонового аудио
            Application.Current.EnteredBackground += (s, e) => { };
            Application.Current.LeavingBackground += (s, e) => { };
            Application.Current.Suspending += (s, e) => {
                // Сохраняем позицию на случай если плеер упадёт после resume
                if (_currentAudioPlayer != null)
                    _currentAudioPosition = _currentAudioPlayer.PlaybackSession.Position;
            };
            Application.Current.Resuming += async (s, e) => {
                // Если плеер упал во время suspend — восстанавливаем
                await System.Threading.Tasks.Task.Delay(1500); // ждём пока AUDIO FAILED придёт
                if (_currentAudioPlayer == null && _currentAudioFilePath != null && _messagesDict.ContainsKey(_currentAudioMsgId)) {
                    var savedMsgId = _currentAudioMsgId;
                    var savedPos = _currentAudioPosition;
                    var savedPath = _currentAudioFilePath;
                    var _ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        try {
                            var item = _messagesDict[savedMsgId];
                            var player = new Windows.Media.Playback.MediaPlayer();
                            player.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
                            var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(savedPath));
                            _currentAudioSource = source;
                            player.Source = source;
                            _currentAudioPlayer = player;
                            _currentAudioMsgId = savedMsgId;
                            SetupPlayer(player, item, savedPos);
                            player.Play();
                        } catch {
                            _currentAudioPlayer = null;
                            _currentAudioSource = null;
                            _currentAudioFilePath = null;
                        }
                    });
                }
            };
        }

        private async System.Threading.Tasks.Task RequestMediaSessionAsync() {
            _mediaSession?.Dispose();
            _mediaSession = null;
            var session = new Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession();
            session.Reason = Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionReason.Unspecified;
            session.Description = "Unogram audio";
            session.Revoked += (s, e) => { };
            var result = await session.RequestExtensionAsync();
            if (result == Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionResult.Allowed)
                _mediaSession = session;
            else
                session.Dispose();
        }
        private void ReleaseMediaSession() {
            _mediaSession?.Dispose();
            _mediaSession = null;
        }

        // Логирование в файл отключено — приложение ничего не пишет на диск
        // для диагностики. Метод оставлен как пустышка, чтобы не переписывать
        // все места, где он вызывается.
        private void Log(string m) { BackgroundService.Diag(m); }

        private async void InitAsync() {
            try {
                var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder;
                var appFolder = await localFolder.CreateFolderAsync("Unogram", CreationCollisionOption.OpenIfExists);
                _dbPath = appFolder.Path.Replace("\\", "/") + "/td_db";
                _filesFolder = await appFolder.CreateFolderAsync("td_db_files", CreationCollisionOption.OpenIfExists);
            } catch (Exception ex) {
                await new Windows.UI.Popups.MessageDialog(Loc.T("err_storage") + ex.Message).ShowAsync();
                return;
            }
            var _lpTask = Task.Run(() => LongPolling());
            // Прокси применяется после инициализации TDLib — см. authorizationStateWaitPhoneNumber
        }


        private async Task FetchAndApplyProxyAsync() {
            List<ProxyEntry> parsed = null;
            try {
                var http = new System.Net.Http.HttpClient();
                http.Timeout = TimeSpan.FromSeconds(10);
                var text = await http.GetStringAsync("https://open-amitie-radio-rs-89235677.koyeb.app/mtproxy.php");
                parsed = new List<ProxyEntry>();
                var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                foreach (var line in lines) {
                    var l = line.Trim();
                    if (string.IsNullOrEmpty(l)) continue;
                    try {
                        if (l.StartsWith("tg://proxy") || l.StartsWith("https://t.me/proxy")) {
                            string query = l.Contains("?") ? l.Substring(l.IndexOf('?') + 1) : "";
                            var qp = new Dictionary<string, string>();
                            foreach (var pair in query.Split('&')) {
                                var kv = pair.Split('=');
                                if (kv.Length == 2) qp[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                            }
                            string server = qp.ContainsKey("server") ? qp["server"] : null;
                            string portStr = qp.ContainsKey("port") ? qp["port"] : null;
                            string secret = qp.ContainsKey("secret") ? qp["secret"] : null;
                            if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(secret) && int.TryParse(portStr, out int port))
                                parsed.Add(new ProxyEntry { Host = server, Port = port, Secret = secret });
                        } else if (l.Contains(":")) {
                            var parts = l.Split(':');
                            if (parts.Length >= 3 && int.TryParse(parts[1], out int port2) && !string.IsNullOrEmpty(parts[2]))
                                parsed.Add(new ProxyEntry { Host = parts[0], Port = port2, Secret = parts[2] });
                        }
                    } catch (Exception ex) { Log("PROXY parse ERR: " + ex.Message); }
                }
            } catch {
                return;
            }
            if (parsed == null || parsed.Count == 0) return;
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                _proxyList = parsed;
                _proxyIndex = 0;
                var t = TryNextProxyAsync(); // fire-and-forget на UI потоке
            });
        }

        private async Task TryNextProxyAsync() {
            if (_proxyList.Count == 0) return;
            if (_proxyIndex >= _proxyList.Count) _proxyIndex = 0;
            var proxy = _proxyList[_proxyIndex];
            await ApplyProxyAsync(proxy.Host, proxy.Port, proxy.Secret);
            // Таймер на 5 секунд — если не подключились, пробуем следующий
            _proxyTimer?.Stop();
            _proxyTimer = new Windows.UI.Xaml.DispatcherTimer();
            _proxyTimer.Interval = TimeSpan.FromSeconds(5);
            _proxyTimer.Tick += async (s, e) => {
                _proxyTimer.Stop();
                if (!_proxyConnected) {
                    _proxyIndex++;
                    await TryNextProxyAsync();
                }
            };
            _proxyTimer.Start();
        }

        private void ClearAllProxies() {
            // Удаляем все известные прокси
            if (_currentProxyId != 0) {
                TdJson.SendUtf8(_client, "{\"@type\":\"removeProxy\",\"proxy_id\":" + _currentProxyId + "}");
                _currentProxyId = 0;
            }
            // Запрашиваем список чтобы удалить все остальные (накопленные)
            TdJson.SendUtf8(_client, "{\"@type\":\"getProxies\"}");
            _proxyConnected = false;
        }

        private async Task ApplyProxyAsync(string host, int port, string secret) {
            ClearAllProxies();
            string reqJson = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                             "\",\"port\":" + port +
                             ",\"type\":{\"@type\":\"proxyTypeMtproto\",\"secret\":\"" + secret + "\"}},\"enable\":true}";
            TdJson.SendUtf8(_client, reqJson);
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                ProxyStatusText.Text = "[..] " + host + ":" + port;
                ProxyStatusText.Visibility = Visibility.Visible;
            });
        }

        private void SendParameters() {
            JObject p = new JObject {
                ["@type"] = "setTdlibParameters",
                ["use_test_dc"] = false,
                ["database_directory"] = _dbPath,
                ["files_directory"] = _filesFolder?.Path.Replace("\\", "/") ?? _dbPath + "_files",
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
            TdJson.SendUtf8(_client, p.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void LongPolling() {
            while (true) {
                // The close is dragging on (no network — TDLib is waiting for the
                // server): leave on the deadline, or exiting the app would hang.
                if (_tdClosing && DateTime.UtcNow >= _tdCloseDeadline) break;
                IntPtr resPtr = TdJson.td_json_client_receive(_client, 1.0);
                if (resPtr == IntPtr.Zero) continue;
                string json = TdJson.IntPtrToStringUtf8(resPtr);
                if (string.IsNullOrEmpty(json)) continue;
                if (_tdClosing) {
                    // The app is already closing: updates are no longer handed
                    // to the UI (the page is coming apart), we only wait for
                    // the confirmation.
                    if (json.Contains("authorizationStateClosed")) break;
                    continue;
                }
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    try {
                        var update = JObject.Parse(json);
                        string type = update["@type"]?.ToString();
                        if (type != "updateOption")
                        HandleUpdate(type, update);
                    } catch (Exception ex) { Log("PARSE ERR: " + ex.Message); }
                });
            }
            // The client itself is not released here: ExitApplicationAsync does
            // that once it sees the reading has stopped. Otherwise the pointer
            // would be zeroed from a foreign thread and the UI could still send
            // a request into an already dead client.
            _pollingStopped.TrySetResult(true);
        }


        private void HandleUpdate(string type, JObject update) {
            switch (type) {
                case "updateAuthorizationState":
                    var s = update["authorization_state"]?["@type"]?.ToString();
                    if (s == "authorizationStateWaitTdlibParameters") {
                        SendParameters();
                        TdJson.SendUtf8(_client, "{\"@type\":\"getOption\",\"name\":\"version\"}");
                    }

                    // Прокси применяется только если пользователь выбрал режим в настройках
                    // _proxyMode == None по умолчанию — автозапуска нет

                    if (s == "authorizationStateWaitPhoneNumber") {
                        LoginStatus.Text = Loc.T("login_enterPhone");
                        PhoneInput.IsEnabled = true;
                        PhoneButton.IsEnabled = true;
                        // Применяем прокси согласно сохранённому режиму
                        if (!_proxyApplied) {
                            _proxyApplied = true;
                            ApplySavedProxy();
                        }
                    }
                    if (s == "authorizationStateWaitCode") {
                        LoginStatus.Text = Loc.T("login_codeSent");
                        PhoneInput.IsEnabled = false;
                        PhoneButton.IsEnabled = false;
                        CodeInput.Visibility = Visibility.Visible;
                        CodeButton.Visibility = Visibility.Visible;
                        CodeInput.Focus(FocusState.Programmatic);
                    }
                    if (s == "authorizationStateWaitPassword") {
                        LoginStatus.Text = Loc.T("login_enter2fa");
                        CodeInput.Visibility = Visibility.Collapsed;
                        CodeButton.Visibility = Visibility.Collapsed;
                        PasswordInput.Visibility = Visibility.Visible;
                        PasswordButton.Visibility = Visibility.Visible;
                        PasswordInput.Focus(FocusState.Programmatic);
                    }
                    if (s == "authorizationStateReady") {
                        _isAuthorized = true;
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        ProxyBottomRow.Visibility = Visibility.Collapsed; // теперь доступно через меню настроек
                        if (SearchPanel != null) SearchPanel.Visibility = Visibility.Visible;
                        if (!_proxyApplied) {
                            _proxyApplied = true;
                            ApplySavedProxy();
                        }
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListArchive\"},\"limit\":1000}");
                        _waitingForMe = true;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getMe\"}");
                        _loadingArchiveIds = true;
                    }
                    if (s == "authorizationStateLoggingOut" || s == "authorizationStateClosed") {
                        _isAuthorized = false;
                        _chatListItems.Clear();
                        _allChatItems.Clear();
                        _chatsDict.Clear();
                        _folderChatIds.Clear();
                        _mainListLoaded = false;
                        ChatListView.Visibility = Visibility.Collapsed;
                        LoginPanel.Visibility = Visibility.Visible;
                        ProxyBottomRow.Visibility = Visibility.Visible;
                        LoginStatus.Text = Loc.T("login_enterPhone");
                        PhoneInput.Text = "";
                        PhoneInput.IsEnabled = true;
                        PhoneButton.IsEnabled = true;
                        CodeInput.Visibility = Visibility.Collapsed;
                        CodeButton.Visibility = Visibility.Collapsed;
                        PasswordInput.Password = "";
                        PasswordInput.Visibility = Visibility.Collapsed;
                        PasswordButton.Visibility = Visibility.Collapsed;
                        LoginStatus.Text = "";
                    }
                    break;

                case "error":
                    string errMsg = update["message"]?.ToString();
                    // Если нет закреплённого сообщения — сбрасываем флаг
                    if (_pinnedMessageId == -1)
                        _pinnedMessageId = 0;
                    // Иначе неудачный поиск внутри чата навсегда "съедал" бы
                    // следующий обычный ответ getChatHistory, приняв его за
                    // результаты поиска.
                    _chatSearchAwaitingResults = false;
                    // Не показываем proxy ошибки в UI
                    if (errMsg != null && (
                        errMsg.Contains("Proxy") ||
                        errMsg.Contains("proxy") ||
                        errMsg.Contains("proxy secret") ||
                        errMsg.Contains("Unsupported proxy"))) {
                        // При невалидном секрете — сразу пробуем следующий прокси
                        if (errMsg.Contains("secret") || errMsg.Contains("non-empty")) {
                            _proxyTimer?.Stop();
                            _proxyIndex++;
                            var skipTask = TryNextProxyAsync();
                        }
                        break;
                    }
                    LoginStatus.Text = Loc.T("login_errorPrefix") + errMsg;
                    PhoneButton.IsEnabled = true;
                    CodeButton.IsEnabled = true;
                    if (_loadingChats && (errMsg?.Contains("CHAT_LIST_EMPTY") ?? false)) {
                        _loadingChats = false;
                    }
                    break;

                case "updateChatAddedToList":
                    // Игнорируем во время начальной загрузки — порядок формирует LoadNextChat.
                    // Реагируем только когда чат реально переходит между списками (архив ↔ главный).
                    if (_loadingChats || _loadingArchive || _loadingArchiveIds) break;
                    long addedChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    string addedList = update["chat_list"]?["@type"]?.ToString() ?? "";
                    if (addedChatId != 0 && _chatsDict.ContainsKey(addedChatId)) {
                        var addedItem = _chatsDict[addedChatId];
                        if (addedList == "chatListMain") {
                            if (_archiveChatItems.Contains(addedItem)) {
                                _archiveChatIds.Remove(addedChatId);
                                _archiveChatItems.Remove(addedItem);
                                UpdateArchiveUnreadBadge();
                            }
                            if (!_chatListItems.Contains(addedItem)) {
                                InsertAfterPinned(_chatListItems, addedItem);
                                ChatCountText.Text = _chatListItems.Count.ToString();
                            }
                        } else if (addedList == "chatListArchive") {
                            if (_chatListItems.Contains(addedItem)) {
                                _chatListItems.Remove(addedItem);
                                _allChatItems.RemoveAll(c => c.Id == addedChatId);
                                ChatCountText.Text = _chatListItems.Count.ToString();
                            }
                            if (!_archiveChatItems.Contains(addedItem)) {
                                _archiveChatIds.Add(addedChatId);
                                InsertAfterPinned(_archiveChatItems, addedItem);
                            }
                        }
                    }
                    break;

                case "updateNewChat":
                    var chatUpd = update["chat"];
                    long chatId = (long)chatUpd["id"];
                    _rawChatsDict[chatId] = chatUpd; // сохраняем сырой JSON для last_read_inbox_message_id
                    // Если пришёл updateNewChat — TDLib уже авторизован (сессия сохранена)
                    if (!_isAuthorized) {
                        _isAuthorized = true;
                        LoginPanel.Visibility = Visibility.Collapsed;
                        ChatListView.Visibility = Visibility.Visible;
                        if (SearchPanel != null) SearchPanel.Visibility = Visibility.Visible;
                        // Pre-fetch архива перед main — как и при обычной авторизации
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListArchive\"},\"limit\":1000}");
                        _loadingArchiveIds = true;
                        _waitingForMe = true;
                        TdJson.SendUtf8(_client, "{\"@type\":\"getMe\"}");
                    }
                    if (!_chatsDict.ContainsKey(chatId)) {
                        bool isChannel = chatUpd["type"]?["@type"]?.ToString() == "chatTypeSupergroup"
                            && (chatUpd["type"]?["is_channel"]?.ToObject<bool>() ?? false);
                        long initOutboxRead = chatUpd["last_read_outbox_message_id"]?.ToObject<long>() ?? 0;
                        string chatTitle = chatUpd["title"]?.ToString();
                        // Чат с собой — называем "⭐ Избранное"
                        bool isSavedMessages = chatUpd["type"]?["@type"]?.ToString() == "chatTypePrivate"
                            && (chatUpd["type"]?["user_id"]?.ToObject<long>() ?? 0) == _myUserId
                            && _myUserId != 0;
                        if (isSavedMessages) chatTitle = Loc.T("menu_favorites");
                        _chatsDict[chatId] = new ChatItem { Id = chatId, Title = chatTitle, OutboxReadId = initOutboxRead > 0 ? initOutboxRead : 0, IsChannel = isChannel, IsSavedMessages = isSavedMessages };
                    }
                    var chatItem = _chatsDict[chatId];
                    // Заполняем последнее сообщение
                    var lastMsg = chatUpd["last_message"];
                    if (lastMsg != null) FillChatLastMessage(chatItem, lastMsg, chatUpd);
                    // Непрочитанные
                    chatItem.UnreadCount = chatUpd["unread_count"]?.ToObject<int>() ?? 0;
                    chatItem.IsMarkedUnread = chatUpd["is_marked_as_unread"]?.ToObject<bool>() ?? false;
                    chatItem.IsMuted = (chatUpd["notification_settings"]?["mute_for"]?.ToObject<int>() ?? 0) > 0;
                    // _archiveChatIds заполняется ДО загрузки главного списка — надёжнее чем positions
                    // (при bump positions уже содержит chatListMain вместо chatListArchive)
                    var positions = chatUpd["positions"] as JArray;
                    bool isArchiveChat = _archiveChatIds.Contains(chatId) ||
                        (positions != null && positions.Any(p => p["list"]?["@type"]?.ToString() == "chatListArchive"));
                    bool isMainChat = !isArchiveChat;
                    // Закреплён ли чат — берём из positions нужного списка
                    if (positions != null) {
                        string targetListType = isArchiveChat ? "chatListArchive" : "chatListMain";
                        var pos = positions.FirstOrDefault(p => p["list"]?["@type"]?.ToString() == targetListType
                                  || p["list"]?["@type"]?.ToString() == "chatListMain");
                        chatItem.IsPinned = pos?["is_pinned"]?.ToObject<bool>() ?? false;
                        long.TryParse(pos?["order"]?.ToString() ?? "0", out long parsedOrder);
                        chatItem.Order = parsedOrder;
                    } else {
                    }
                    if (_pendingPinnedPositions.ContainsKey(chatId)) {
                        bool pendingPin = _pendingPinnedPositions[chatId];
                        chatItem.IsPinned = pendingPin;
                        _pendingPinnedPositions.Remove(chatId);
                    }

                    // updateNewChat только обновляет _chatsDict.
                    // Добавление в видимый список — исключительно через LoadNextChat (100ms throttle).
                    // Исключение: если это ответ на getChat из else-ветки LoadNextChat — продолжаем цепочку.
                    if (_pendingGetChat.Contains(chatId)) {
                        _pendingGetChat.Remove(chatId);
                        LoadNextChat(); // продолжаем очередь
                    }
                    var phSmall = chatUpd["photo"]?["small"];
                    if (phSmall != null) {
                        long phFileId = (long)phSmall["id"];
                        _fileToChatId[phFileId] = chatId;
                        string phPath = phSmall["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(phPath))
                            { var t = UpdateAvatar(chatId, phPath); }
                        else
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + phFileId + ",\"priority\":1,\"synchronous\":false}");
                    }
                    break;

                case "updateFile":
                case "file":
                    var fileObj = (type == "updateFile") ? update["file"] as JObject : update;
                    if (fileObj != null) {
                        long fid = fileObj["id"] != null ? (long)fileObj["id"] : 0;
                        string fpath = fileObj["local"]?["path"]?.ToString();
                        bool isCompleted = fileObj["local"]?["is_downloading_completed"]?.ToObject<bool>() ?? false;
                        bool isUploaded  = fileObj["remote"]?["is_uploading_completed"]?.ToObject<bool>() ?? false;
                        long downloaded = fileObj["local"]?["downloaded_size"]?.ToObject<long>() ?? 0;
                        long total = fileObj["size"]?.ToObject<long>() ?? 0;

                        // Обработка uploadFile — отправляем сообщение когда файл загружен
                        if (isUploaded && fid != 0 && !string.IsNullOrEmpty(_pendingUploadType) && _pendingUploadChatId != 0) {
                            string uType = _pendingUploadType;
                            long uChatId = _pendingUploadChatId;
                            _pendingUploadType = "";
                            _pendingUploadChatId = 0;
                            string sendReq;
                            if (uType == "doc") {
                                sendReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + uChatId +
                                    ",\"input_message_content\":{\"@type\":\"inputMessageDocument\"" +
                                    ",\"document\":{\"@type\":\"inputDocument\"" +
                                    ",\"document\":{\"@type\":\"inputFileId\",\"id\":" + fid + "}" +
                                    ",\"disable_content_type_detection\":false}" +
                                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                            } else if (uType.StartsWith("voice_")) {
                                int dur = int.TryParse(uType.Replace("voice_",""), out int d) ? d : 0;
                                sendReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + uChatId +
                                    ",\"input_message_content\":{\"@type\":\"inputMessageVoiceNote\"" +
                                    ",\"voice_note\":{\"@type\":\"inputVoiceNote\"" +
                                    ",\"voice_note\":{\"@type\":\"inputFileId\",\"id\":" + fid + "}" +
                                    ",\"duration\":" + dur +
                                    ",\"waveform\":\"\"}" +
                                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                            } else sendReq = null;
                            if (sendReq != null) {
                                TdJson.SendUtf8(_client, sendReq);
                            }
                        }
                        if (fid != 0) {
                            // Upload прогресс
                            bool isUploadingActive = fileObj["remote"]?["is_uploading_active"]?.ToObject<bool>() ?? false;
                            long uploaded = fileObj["remote"]?["uploaded_size"]?.ToObject<long>() ?? 0;
                            long fileSize = fileObj["size"]?.ToObject<long>() ?? 0;
                            long expectedSize = fileObj["expected_size"]?.ToObject<long>() ?? fileSize;
                            long totalSize = expectedSize > 0 ? expectedSize : fileSize;
                            if (_uploadFileToMsgId.ContainsKey(fid)) {
                                long upMsgId = _uploadFileToMsgId[fid];
                                if (_messagesDict.ContainsKey(upMsgId)) {
                                    var upItem = _messagesDict[upMsgId];
                                    if (isUploaded) {
                                        if (!string.IsNullOrEmpty(fpath)) {
                                            _uploadFileToMsgId.Remove(fid);
                                            upItem.DownloadStatus = "";
                                            var tu = UpdateMessagePhoto(upMsgId, fpath);
                                        }
                                        // Если путь ещё не готов — не убираем регистрацию, чтобы
                                        // её нашло следующее updateFile для этого же fid.
                                    } else if (isUploadingActive || uploaded > 0) {
                                        if (totalSize > 0) {
                                            int pct = (int)Math.Min(99, uploaded * 100 / totalSize);
                                            upItem.DownloadStatus = "⬆ " + pct + "%";
                                        } else {
                                            upItem.DownloadStatus = "⬆ ...";
                                        }
                                    }
                                }
                            }

                            if (_fileToChatId.ContainsKey(fid) && !string.IsNullOrEmpty(fpath))
                                { var t = UpdateAvatar(_fileToChatId[fid], fpath); }

                            if (isCompleted && _fileToSenderUserId.ContainsKey(fid) && !string.IsNullOrEmpty(fpath)) {
                                long avatarUid = _fileToSenderUserId[fid];
                                _fileToSenderUserId.Remove(fid);
                                var ts = UpdateSenderAvatar(avatarUid, fpath);
                            }

                            // Аватарка для результатов поиска
                            if (isCompleted && !string.IsNullOrEmpty(fpath) && _fileToSearchResult.ContainsKey(fid)) {
                                var srItm = _fileToSearchResult[fid];
                                _fileToSearchResult.Remove(fid);
                                { var t = UpdateAvatarSearchResult(srItm, fpath); }
                            }

                            // Thumbnail для панели стикеров
                            if (isCompleted && !string.IsNullOrEmpty(fpath) && _stickerThumbToItem.ContainsKey(fid))
                                HandleStickerThumbDownloaded(fid, fpath);

                            // Отправка стикера — ждём пока файл скачается
                            if (isCompleted && fid == _pendingStickerFileId && _pendingStickerChatId != 0) {
                                long sChatId   = _pendingStickerChatId;
                                long sFileId   = _pendingStickerFileId;
                                long sThreadId = _threadMessageId;
                                _pendingStickerFileId = 0;
                                _pendingStickerChatId = 0;
                                string sReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + sChatId +
                                    (sThreadId != 0 ? ",\"topic_id\":{\"@type\":\"messageTopicThread\",\"message_thread_id\":" + sThreadId + "}" +
                                                      ",\"message_thread_id\":" + sThreadId : "") +
                                    ",\"input_message_content\":{\"@type\":\"inputMessageSticker\"" +
                                    ",\"sticker\":{\"@type\":\"inputSticker\"" +
                                    ",\"sticker\":{\"@type\":\"inputFileId\",\"id\":" + sFileId + "}" +
                                    ",\"width\":512,\"height\":512}}}";
                                TdJson.SendUtf8(_client, sReq);
                            }

                            // Фолбэк для стикеров: TDLib может вернуть новый file_id при скачивании.
                            if (!_fileToMsgId.ContainsKey(fid) && isCompleted && !string.IsNullOrEmpty(fpath)
                                && (fpath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                                 || fpath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))) {
                                string remoteUid = fileObj["remote"]?["unique_id"]?.ToString();
                                if (!string.IsNullOrEmpty(remoteUid) && _remoteUniqueIdToMsgId.ContainsKey(remoteUid)) {
                                    long mid2 = _remoteUniqueIdToMsgId[remoteUid];
                                    _fileToMsgId[fid] = mid2;
                                    var t2 = UpdateMessagePhoto(mid2, fpath);
                                }
                            }

                            if (_fileToMsgId.ContainsKey(fid)) {
                                long mid = _fileToMsgId[fid];
                                bool isImg = !string.IsNullOrEmpty(fpath) &&
                                    (fpath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                     fpath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                                if (isImg && (!_inlinePhotoFileId.ContainsKey(mid) || _inlinePhotoFileId[mid] == fid))
                                    { var t = UpdateMessagePhoto(mid, fpath); }
                                // Миниатюра видео-стикера — это mp4-клип, не картинка
                                if (isCompleted && _stickerVideoFileIds.Contains(fid) && !string.IsNullOrEmpty(fpath) && _messagesDict.ContainsKey(mid)) {
                                    _messagesDict[mid].IsStickerVideo = true;
                                    _messagesDict[mid].StickerVideoSource = new Uri(fpath);
                                }
                                // Если это полноразмерное фото для оверлея
                                if (isCompleted && isImg && _fullPhotoMsgId == mid && !string.IsNullOrEmpty(fpath))
                                    { var t = ShowFullPhoto(fpath); }
                                if (_messagesDict.ContainsKey(mid)) {
                                    var msgItem = _messagesDict[mid];
                                    if (msgItem.IsGif) {
                                        bool isGifFile = _videoFileIds.ContainsKey(fid);
                                        if (isCompleted && isGifFile && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.GifSource = new Uri(fpath);
                                            msgItem.VideoDownloadProgress = null;
                                        } else if (isGifFile && total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.VideoDownloadProgress = "⏳ " + pct + "%";
                                        }
                                    } else if (msgItem.IsVideo) {
                                        bool isVideoFile = _videoFileIds.ContainsKey(fid);
                                        if (isCompleted && isVideoFile && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.VideoDownloadProgress = null;
                                            if (_pendingSaveMsgIds.Remove(mid)) SaveAndToast(fpath, Windows.Storage.Pickers.PickerLocationId.VideosLibrary);
                                        } else if (isVideoFile && total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.VideoDownloadProgress = "⏳ " + pct + "%";
                                        }
                                    }
                                    if (msgItem.IsDocument) {
                                        if (isCompleted && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.IsDownloaded = true;
                                            msgItem.DownloadStatus = Loc.T("status_open");
                                        } else if (total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.DownloadStatus = "⏳ " + pct + "%";
                                        }
                                    }
                                    if (msgItem.IsAudio) {
                                        if (isCompleted && !string.IsNullOrEmpty(fpath)) {
                                            msgItem.FilePath = fpath;
                                            msgItem.AudioPlayStatus = "▶";
                                            if (_pendingSaveMsgIds.Remove(mid)) SaveAndToast(fpath, Windows.Storage.Pickers.PickerLocationId.MusicLibrary);
                                        } else if (total > 0) {
                                            int pct = (int)(downloaded * 100 / total);
                                            msgItem.AudioPlayStatus = "⏳" + pct + "%";
                                        }
                                    }
                                }
                            }
                        }
                    }
                    break;

                case "updateDeleteMessages":
                    // Сообщение удалено (в том числе "у всех") — снимаем уведомление,
                    // иначе оно живёт в центре уведомлений дольше самого сообщения.
                    if (update["is_permanent"]?.ToObject<bool>() ?? false) {
                        long delChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                        if (delChatId != 0) RemoveToastsForChat(delChatId);
                    }
                    break;

                case "updateNewMessage":
                    var newMsg = update["message"];
                    long newMsgChatId = newMsg?["chat_id"]?.ToObject<long>() ?? 0;
                    // Пришло сообщение в чат, которого нет в списке — вернём его.
                    if (newMsgChatId != 0) EnsureChatInList(newMsgChatId);
                    bool newMsgOutgoing = newMsg?["is_outgoing"]?.ToObject<bool>() ?? false;
                    string newMsgType = newMsg?["content"]?["@type"]?.ToString() ?? "";
                    // Для исходящих файлов — регистрируем upload прогресс даже если чат не открыт
                    if (newMsgOutgoing && newMsg != null) {
                        var newContent = newMsg["content"];
                        long newMsgId = newMsg["id"]?.ToObject<long>() ?? 0;
                        if (newMsgType == "messagePhoto") {
                            var sizes = newContent["photo"]?["sizes"] as JArray;
                            if (sizes != null && sizes.Count > 0) {
                                var ft = sizes[sizes.Count - 1]["photo"] as JObject;
                                if (ft != null) {
                                    long fid2 = ft["id"]?.ToObject<long>() ?? 0;
                                    bool uploaded2 = ft["remote"]?["is_uploading_completed"]?.ToObject<bool>() ?? false;
                                    if (fid2 != 0 && !uploaded2) {
                                        _uploadFileToMsgId[fid2] = newMsgId;
                                    }
                                }
                            }
                        } else if (newMsgType == "messageDocument") {
                            var docF = newContent["document"]?["document"] as JObject;
                            if (docF != null) {
                                long fid2 = docF["id"]?.ToObject<long>() ?? 0;
                                bool uploaded2 = docF["remote"]?["is_uploading_completed"]?.ToObject<bool>() ?? false;
                                if (fid2 != 0 && !uploaded2) {
                                    _uploadFileToMsgId[fid2] = newMsgId;
                                }
                            }
                        } else if (newMsgType == "messageVideoNote") {
                            var vnF = newContent["video_note"]?["video"] as JObject;
                            if (vnF != null) {
                                long fid2 = vnF["id"]?.ToObject<long>() ?? 0;
                                bool uploaded2 = vnF["remote"]?["is_uploading_completed"]?.ToObject<bool>() ?? false;
                                if (fid2 != 0 && !uploaded2) {
                                    _uploadFileToMsgId[fid2] = newMsgId;
                                }
                            }
                        }
                    }
                    if (newMsgChatId == _currentChatId && newMsg != null && !_isLoadingHistory) {
                        var newItem = ParseMessage(newMsg, trustEditDate: false);
                        if (newItem != null) {
                            var lastReal = _messageItems.LastOrDefault(m => !m.IsSeparator);
                            if (lastReal == null || lastReal.RawDate.Date != newItem.RawDate.Date)
                                _messageItems.Add(MakeSeparator(newItem.RawDate.Date, DateTime.Today));
                            _messageItems.Add(newItem);
                            // Группировка альбома с предыдущим сообщением (если это фото из той же пачки)
                            bool sameAlbum = lastReal != null && !string.IsNullOrEmpty(newItem.AlbumId) && newItem.AlbumId != "0" && newItem.AlbumId == lastReal.AlbumId;
                            newItem.IsFirstInGroup = !sameAlbum;
                            if (sameAlbum) lastReal.IsLastInGroup = false;
                            newItem.IsLastInGroup = true;
                            StartBotButton.Visibility = Visibility.Collapsed;

                            double scrollable3 = MessagesScrollViewer.ScrollableHeight;
                            double offset2 = MessagesScrollViewer.VerticalOffset;
                            bool wasAtBottom = scrollable3 <= 0 || (scrollable3 - offset2) < 200;
                            // Своё отправленное сообщение — всегда уводим вниз, как в оригинале,
                            // даже если до этого пролистали историю вверх. Для чужих входящих
                            // сообщений оставляем прежнее поведение — не дёргаем скролл, если
                            // человек специально ушёл читать историю выше.
                            if (wasAtBottom || newItem.IsOutgoing) {
                                var t = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                                t.Tick += (ts, te) => { t.Stop(); MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight + 1000, null, false); };
                                t.Start();
                            }
                        }
                        // Помечаем как прочитанное если чат открыт
                        long newMsgId = newMsg["id"]?.ToObject<long>() ?? 0;
                        if (newMsgId != 0)
                            TdJson.SendUtf8(_client, "{\"@type\":\"viewMessages\",\"chat_id\":" + newMsgChatId + ",\"message_ids\":[" + newMsgId + "],\"force_read\":true}");
                    }
                    // Обновляем бейдж архива если сообщение пришло в архивный чат
                    if (_archiveChatItems.Any(ch => ch.Id == newMsgChatId))
                        UpdateArchiveUnreadBadge();
                    // Звук и уведомление для входящих личных сообщений
                    if (!_soundEnabled && newMsg != null && BackgroundService.IsCatchUpRunning)
                        BackgroundService.Diag("Toast skipped: sound disabled");
                    if (_soundEnabled && newMsg != null) {
                        bool isOutgoing = newMsg["is_outgoing"]?.ToObject<bool>() ?? false;
                        bool isMuted    = _chatsDict.ContainsKey(newMsgChatId) && _chatsDict[newMsgChatId].IsMuted;
                        long sentAt     = newMsg["date"]?.ToObject<long>() ?? 0;
                        long toastMsgId = newMsg["id"]?.ToObject<long>() ?? 0;
                        // На переднем плане уведомляем только о свежем, иначе при
                        // открытии приложения сыплется весь backlog. В фоне же
                        // задача догрузки для того и нужна, чтобы сообщить о том,
                        // что пришло, пока телефон спал — там планка шире, а от
                        // повторов защищает отметка последнего уведомления.
                        bool background = BackgroundService.IsCatchUpRunning
                                       || !BackgroundService.IsInForeground;
                        int  maxAge     = background ? CatchUpToastMaxAgeSeconds : ToastMaxAgeSeconds;
                        bool isFresh    = sentAt == 0 ||
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAt <= maxAge;
                        bool notYetSeen = BackgroundService.ShouldNotify(newMsgChatId, toastMsgId);
                        // The message belongs to the chat the user is reading, so
                        // there is nothing to announce: no sound, no banner and no
                        // notification-bar glyph, which means raising no toast at
                        // all. The on-screen test is what makes this safe — a chat
                        // left open while the app is minimised must still notify.
                        bool inOpenChatOnScreen = newMsgChatId == _currentChatId
                                               && BackgroundService.IsAppOnScreen;
                        bool shouldToast = !isOutgoing && !isMuted && isFresh
                                        && notYetSeen && !inOpenChatOnScreen;
                        // В фоне записываем, почему уведомление не показано —
                        // иначе отсутствие toast'а невозможно отличить от причин.
                        if (background && !shouldToast)
                            BackgroundService.Diag("Toast skipped: sound=" + _soundEnabled
                                + " outgoing=" + isOutgoing + " muted=" + isMuted
                                + " fresh=" + isFresh + " age=" + (sentAt == 0 ? -1 :
                                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sentAt)
                                + " notYetSeen=" + notYetSeen + " chat=" + newMsgChatId
                                + " inOpenChat=" + inOpenChatOnScreen);
                        if (shouldToast) {
                            // Собираем имя и текст для уведомления
                            string senderName = "";
                            if (_chatsDict.ContainsKey(newMsgChatId))
                                senderName = _chatsDict[newMsgChatId].Title;
                            else if (_usersDict.ContainsKey(newMsgChatId)) {
                                var u = _usersDict[newMsgChatId];
                                senderName = (u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim();
                            }
                            var mc = newMsg["content"];
                            string msgText = mc?["text"]?["text"]?.ToString()
                                          ?? mc?["caption"]?["text"]?.ToString()
                                          ?? (mc?["@type"]?.ToString()?.Replace("message","") ?? Loc.T("media_message"));
                            ShowToastNotification(senderName, msgText, newMsgChatId, true);
                            AddTilePreviewAndUpdate(newMsgChatId, senderName, msgText);
                        }
                    }
                    break;

                case "updateChatFolders":
                    var folders = update["chat_folders"] as Newtonsoft.Json.Linq.JArray;
                    if (folders != null) {
                        var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => BuildFolderTabs(folders));
                    }
                    break;

                case "updateCall":
                    Calls.HandleUpdateCall(update);
                    break;
                case "updateConnectionState":
                    var connState = update["state"]?["@type"]?.ToString();
                    if (connState == "connectionStateReady") {
                        _connectionReady = true;
                        _proxyConnected = true;
                        _proxyTimer?.Stop();
                        _connectingTimer?.Stop();
                        ConnectionStatusText.Text = "";
                        ConnectionProgressRing.IsActive = false;
                        ConnectionProgressRing.Visibility = Visibility.Collapsed;
                        if (_currentProxyId != 0) {
                            ProxyStatusText.Text = ProxyStatusText.Text.Replace("[..] ", "[ok] ");
                            ProxyStatusText.Visibility = Visibility.Visible;
                        }
                    } else {
                        _connectionReady = false;
                        bool spinning = connState == "connectionStateConnecting"
                                     || connState == "connectionStateConnectingToProxy"
                                     || connState == "connectionStateUpdating";
                        string connText = connState == "connectionStateConnecting"          ? Loc.T("conn_connecting")
                            : connState == "connectionStateConnectingToProxy"               ? Loc.T("conn_connectingProxy")
                            : connState == "connectionStateUpdating"                        ? Loc.T("conn_updating")
                            : connState == "connectionStateWaitingForNetwork"               ? Loc.T("conn_noNetwork")
                            : "...";
                        ConnectionStatusText.Text = connText;
                        ConnectionProgressRing.IsActive = spinning;
                        ConnectionProgressRing.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
                        // Если подключение через прокси зависло — через 10с пробуем следующий
                        if ((connState == "connectionStateConnecting" ||
                             connState == "connectionStateConnectingToProxy") &&
                            _proxyList.Count > 0) {
                            _connectingTimer?.Stop();
                            _connectingTimer = new Windows.UI.Xaml.DispatcherTimer();
                            _connectingTimer.Interval = TimeSpan.FromSeconds(10);
                            _connectingTimer.Tick += async (s2, e2) => {
                                _connectingTimer.Stop();
                                if (!_connectionReady && _proxyList.Count > 0) {
                                    _proxyTimer?.Stop();
                                    _proxyIndex++;
                                    await TryNextProxyAsync();
                                }
                            };
                            _connectingTimer.Start();
                        } else {
                            _connectingTimer?.Stop();
                        }
                    }
                    break;

                case "addedProxies":
                case "proxies":
                    var proxyItems = update["proxies"] as JArray;
                    if (proxyItems != null) {
                        // Удаляем все прокси кроме текущего активного
                        foreach (var pi in proxyItems) {
                            int pid = pi["id"]?.ToObject<int>() ?? 0;
                            if (pid != 0 && pid != _currentProxyId)
                                TdJson.SendUtf8(_client, "{\"@type\":\"removeProxy\",\"proxy_id\":" + pid + "}");
                        }
                    }
                    break;

                case "addedProxy":
                    long newProxyId = update["id"]?.ToObject<long>() ?? 0;
                    if (newProxyId != 0) {
                        _currentProxyId = (int)newProxyId;
                        var proxyObj = update["proxy"];
                        string ph = proxyObj?["server"]?.ToString() ?? "";
                        int pp = proxyObj?["port"]?.ToObject<int>() ?? 0;
                        string status = _connectionReady ? "[ok] " : "[..] ";
                        var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                            ProxyStatusText.Text = status + ph + ":" + pp;
                            ProxyStatusText.Visibility = Visibility.Visible;
                        });
                    }
                    break;

                case "user":
                    long gUid = update["id"]?.ToObject<long>() ?? 0;
                    if (gUid != 0) {
                        _usersDict[gUid] = update;
                        // Ответ на getMe
                        if (_waitingForMe) {
                            _waitingForMe = false;
                            _myUserId = gUid;
                            // Переименовываем чат с собой в списке чатов
                            if (_chatsDict.ContainsKey(_myUserId)) {
                                _chatsDict[_myUserId].Title = Loc.T("menu_favorites");
                                _chatsDict[_myUserId].IsSavedMessages = true;
                            }
                            if (_contactsPendingMyId && _contactItems != null) {
                                _contactsPendingMyId = false;
                                var selfContact = _contactItems.FirstOrDefault(c => c.UserId == gUid);
                                if (selfContact != null) {
                                    selfContact.FullName = Loc.T("menu_favorites");
                                    selfContact.Username = "";
                                    selfContact.LastSeen = "";
                                }
                            }
                        }
                        if (gUid == _currentChatId)
                            UpdateChatStatus(update["status"]);
                        // Обновляем контакт если он в списке контактов
                        var matchContact = _contactItems.FirstOrDefault(ctItem => ctItem.UserId == gUid);
                        if (matchContact != null) {
                            string fn = (update["first_name"]?.ToString() + " " + update["last_name"]?.ToString()).Trim();
                            matchContact.FullName = string.IsNullOrEmpty(fn) ? gUid.ToString() : fn;
                            matchContact.Username = update["username"]?.ToString() ?? update["usernames"]?["editable_username"]?.ToString() ?? "";
                            { var t = LoadContactAvatarFromUser(matchContact, update); }
                        }
                    }
                    break;

                case "updateSupergroup":
                    var sg = update["supergroup"];
                    if (sg != null) {
                        long sgId = sg["id"]?.ToObject<long>() ?? 0;
                        if (sgId != 0) _supergroupDict[sgId] = sg;
                    }
                    break;

                case "supergroup": {
                    long sg2Id = update["id"]?.ToObject<long>() ?? 0;
                    if (sg2Id != 0) {
                        _supergroupDict[sg2Id] = update;
                        // Если это текущий чат — показываем число участников
                        if (_rawChatsDict.ContainsKey(_currentChatId)) {
                            var rawC3 = _rawChatsDict[_currentChatId] as Newtonsoft.Json.Linq.JObject;
                            long curSgId = rawC3?["type"]?["supergroup_id"]?.ToObject<long>() ?? 0;
                            if (curSgId == sg2Id) {
                                int memberCount = update["member_count"]?.ToObject<int>() ?? 0;
                                if (memberCount > 0) {
                                    bool isChannel = update["is_channel"]?.ToObject<bool>() ?? false;
                                    CurrentChatStatus.Text = memberCount + (isChannel ? Loc.T("label_subscribers") : Loc.T("label_members"));
                                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#707070" : "#CCE8FF");
                                }
                            }
                        }
                    }
                    break;
                }

                case "basicGroup": {
                    long bgId = update["id"]?.ToObject<long>() ?? 0;
                    if (bgId != 0 && _rawChatsDict.ContainsKey(_currentChatId)) {
                        var rawC4 = _rawChatsDict[_currentChatId] as Newtonsoft.Json.Linq.JObject;
                        long curBgId = rawC4?["type"]?["basic_group_id"]?.ToObject<long>() ?? 0;
                        if (curBgId == bgId) {
                            int memberCount = update["member_count"]?.ToObject<int>() ?? 0;
                            if (memberCount > 0) {
                                CurrentChatStatus.Text = memberCount + Loc.T("label_members");
                                CurrentChatStatus.Foreground = CB(_isLightTheme ? "#707070" : "#CCE8FF");
                            }
                        }
                    }
                    break;
                }

                case "updateUser":
                    var user = update["user"];
                    long uid = user?["id"]?.ToObject<long>() ?? 0;
                    if (uid != 0) {
                        _usersDict[uid] = user;
                        if (_chatsDict.ContainsKey(uid)) {
                            string uStatus = user["status"]?["@type"]?.ToString();
                            _chatsDict[uid].IsOnline = uStatus == "userStatusOnline";
                        }
                        // Обновляем шапку если открыт чат с этим пользователем
                        if (uid == _currentChatId)
                            UpdateChatStatus(user["status"]);
                    }
                    break;

                case "updateChatAction":
                    long actionChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    string actionType = update["action"]?["@type"]?.ToString() ?? "";
                    if (actionChatId == _currentChatId && actionType == "chatActionTyping") {
                        CurrentChatStatus.Text = Loc.T("status_typing");
                        CurrentChatStatus.Foreground = CB("#2AABEE");
                        _typingTimer.Stop();
                        _typingTimer.Start();
                    }
                    break;

                case "updateUserStatus":
                    long userId = update["user_id"]?.ToObject<long>() ?? 0;
                    string statusType = update["status"]?["@type"]?.ToString();
                    bool isOnline = statusType == "userStatusOnline";
                    // expires — серверное время, используем для калибровки часов
                    if (isOnline) {
                        long expires = update["status"]?["expires"]?.ToObject<long>() ?? 0;
                        if (expires > 0) UpdateServerTimeOffset(expires - 30); // expires = now+30s на сервере
                    }
                    if (_chatsDict.ContainsKey(userId))
                        _chatsDict[userId].IsOnline = isOnline;
                    // Синхронизируем статус в _usersDict чтобы при открытии чата был актуальный
                    if (_usersDict.ContainsKey(userId) && update["status"] != null)
                        _usersDict[userId]["status"] = update["status"];
                    if (userId == _currentChatId) {
                        long wo = update["status"]?["was_online"]?.ToObject<long>() ?? 0;
                        long nowUnix = LocalUnixNow();
                        UpdateChatStatus(update["status"]);
                    }
                    break;

                case "updateChatLastMessage":
                    long ulcId = update["chat_id"]?.ToObject<long>() ?? 0;
                    var ulcMsg = update["last_message"];
                    if (ulcId != 0 && ulcMsg != null) EnsureChatInList(ulcId);
                    // Иначе закешированный _rawChatsDict[chatId]["last_message"] протухает
                    // на каждом новом сообщении в активном чате — раньше это ломало,
                    // например, "Пометить как прочитанное" (брало last_message.id
                    // оттуда и получало 0/устаревшее значение).
                    if (ulcId != 0 && ulcMsg != null && _rawChatsDict.ContainsKey(ulcId)) {
                        var rawForLastMsg = _rawChatsDict[ulcId] as JObject;
                        if (rawForLastMsg != null) rawForLastMsg["last_message"] = ulcMsg;
                    }
                    if (ulcId != 0 && ulcMsg != null && _chatsDict.ContainsKey(ulcId)) {
                        string ulcType = ulcMsg["content"]?["@type"]?.ToString() ?? "null";
                        FillChatLastMessage(_chatsDict[ulcId], ulcMsg, update);
                        MoveChatToTop(ulcId);
                    }
                    break;

                case "updateChatPosition":
                    long ucpId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucpId != 0) {
                        var ucpPos = update["position"];
                        string ucpListType = ucpPos?["list"]?["@type"]?.ToString() ?? "";
                        // Игнорируем позиции папок — они не влияют на закрепление в основном списке
                        if (ucpListType == "chatListFolder") break;
                        bool ucpPinned = ucpPos?["is_pinned"]?.ToObject<bool>() ?? false;
                        long.TryParse(ucpPos?["order"]?.ToString() ?? "0", out long ucpOrderVal);
                        if (_chatsDict.ContainsKey(ucpId)) {
                            _chatsDict[ucpId].IsPinned = ucpPinned;
                            _chatsDict[ucpId].Order = ucpOrderVal;
                            var allItem = _allChatItems.FirstOrDefault(ch => ch.Id == ucpId);
                            if (allItem != null) { allItem.IsPinned = ucpPinned; allItem.Order = ucpOrderVal; }
                            var ucpList = _archiveChatItems.Any(ch => ch.Id == ucpId) ? _archiveChatItems : _chatListItems;
                            var ucpItem = ucpList.FirstOrDefault(ch => ch.Id == ucpId);
                            if (ucpItem != null) {
                                ucpList.Remove(ucpItem);
                                if (ucpPinned) InsertAfterPinned(ucpList, ucpItem);
                                else InsertBySortOrder(ucpList, ucpItem);
                            }
                        } else {
                            _pendingPinnedPositions[ucpId] = ucpPinned;
                            // Ненулевой порядок означает, что чат снова в списке.
                            string ucpOrder = ucpPos?["order"]?.ToString() ?? "0";
                            if (ucpOrder != "0") EnsureChatInList(ucpId);
                        }
                    }
                    break;

                case "updateChatLastPinnedMessageId":
                    long pinnedChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long newPinnedId  = update["pinned_message_id"]?.ToObject<long>() ?? 0;
                    // Обновляем rawChatsDict
                    if (pinnedChatId != 0 && _rawChatsDict.ContainsKey(pinnedChatId)) {
                        var rawC = _rawChatsDict[pinnedChatId] as Newtonsoft.Json.Linq.JObject;
                        if (rawC != null) rawC["pinned_message_id"] = newPinnedId;
                    }
                    // Если это текущий чат — обновляем полоску
                    if (pinnedChatId == _currentChatId) {
                        _pinnedMessageId = newPinnedId;
                        if (newPinnedId == 0) {
                            PinnedMessageBar.Visibility = Visibility.Collapsed;
                            PinnedMessageText.Text = "";
                        } else {
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + pinnedChatId + ",\"message_id\":" + newPinnedId + "}");
                        }
                    }
                    break;

                case "updateChatReadInbox":
                    long ucriId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucriId != 0 && _chatsDict.ContainsKey(ucriId)) {
                        _chatsDict[ucriId].UnreadCount = update["unread_count"]?.ToObject<int>() ?? 0;
                        if (_chatsDict[ucriId].UnreadCount == 0)
                            _chatsDict[ucriId].IsMarkedUnread = false;
                        if (_archiveChatItems.Any(ch => ch.Id == ucriId))
                            UpdateArchiveUnreadBadge();
                        // Убираем разделитель "Новые сообщения" если это текущий чат
                        if (ucriId == _currentChatId) {
                            long newLastRead = update["last_read_inbox_message_id"]?.ToObject<long>() ?? 0;
                            // Ищем и удаляем разделитель если сообщения прочитаны
                            var sepIdx = -1;
                            for (int si = 0; si < _messageItems.Count; si++) {
                                if (_messageItems[si].IsUnreadSeparator) { sepIdx = si; break; }
                            }
                            if (sepIdx >= 0 && newLastRead > 0) {
                                _messageItems.RemoveAt(sepIdx);
                            }
                        }
                        // Обновляем rawChatsDict для следующего открытия чата
                        if (_rawChatsDict.ContainsKey(ucriId)) {
                            var raw = _rawChatsDict[ucriId] as JObject;
                            if (raw != null)
                                raw["last_read_inbox_message_id"] = update["last_read_inbox_message_id"];
                        }
                    }
                    break;

                case "updateChatIsMarkedAsUnread":
                    long ucimId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucimId != 0 && _chatsDict.ContainsKey(ucimId))
                        _chatsDict[ucimId].IsMarkedUnread = update["is_marked_as_unread"]?.ToObject<bool>() ?? false;
                    break;

                case "updateChatNotificationSettings":
                    // Приходит и в ответ на наш setChatNotificationSettings, и при
                    // изменении настроек с другого устройства — статус хранится
                    // на сервере Telegram, а не только локально.
                    long ucnsId = update["chat_id"]?.ToObject<long>() ?? 0;
                    if (ucnsId != 0 && _chatsDict.ContainsKey(ucnsId)) {
                        int muteFor = update["notification_settings"]?["mute_for"]?.ToObject<int>() ?? 0;
                        _chatsDict[ucnsId].IsMuted = muteFor > 0;
                    }
                    break;

                case "updateUnreadChatCount":
                    if (update["chat_list"]?["@type"]?.ToString() == "chatListMain") {
                        int totalUnread = update["unread_unmuted_count"]?.ToObject<int>() ?? 0;
                        if (totalUnread == 0)
                            totalUnread = update["unread_count"]?.ToObject<int>() ?? 0;
                        UpdateAppBadge(totalUnread);
                        // Если всё прочитано — очищаем бейдж и плитку
                        if (totalUnread == 0) {
                            Windows.UI.Notifications.BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear();
                            ClearLiveTile();
                        }
                    }
                    // TDLib присылает готовый счётчик непрочитанных при старте — используем для бейджа архива
                    if (update["chat_list"]?["@type"]?.ToString() == "chatListArchive") {
                        int archiveUnread = update["unread_unmuted_count"]?.ToObject<int>() ?? 0;
                        if (archiveUnread == 0)
                            archiveUnread = update["unread_count"]?.ToObject<int>() ?? 0;
                        if (archiveUnread > 0) {
                            ArchiveUnreadText.Text = archiveUnread > 99 ? "99+" : archiveUnread.ToString();
                            ArchiveUnreadBadge.Visibility = Visibility.Visible;
                            ArchiveArrow.Visibility = Visibility.Collapsed;
                        } else {
                            ArchiveUnreadBadge.Visibility = Visibility.Collapsed;
                            ArchiveArrow.Visibility = Visibility.Visible;
                        }
                    }
                    break;

                case "updateMessageInteractionInfo":
                    long umiChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long umiMsgId = update["message_id"]?.ToObject<long>() ?? 0;
                    if (umiChatId == _currentChatId && _messagesDict.ContainsKey(umiMsgId)) {
                        var reacts = update["interaction_info"]?["reactions"]?["reactions"] as JArray;
                        _messagesDict[umiMsgId].Reactions = reacts != null && reacts.Count > 0
                            ? BuildReactionsString(reacts) : "";
                        var replyInfo = update["interaction_info"]?["reply_info"];
                        if (replyInfo != null)
                            _messagesDict[umiMsgId].ReplyCount = replyInfo["reply_count"]?.ToObject<int>() ?? 0;
                    }
                    break;

                case "updateChatReadOutbox":
                    long ucrId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long ucrMsgId = update["last_read_outbox_message_id"]?.ToObject<long>() ?? 0;
                    if (ucrId != 0 && ucrMsgId > 0 && _chatsDict.ContainsKey(ucrId)) {
                        _chatsDict[ucrId].IsRead = true;
                        _chatsDict[ucrId].OutboxReadId = ucrMsgId;
                    }
                    // Обновляем галочки в открытом чате
                    if (ucrId == _currentChatId && ucrMsgId > 0) {
                        _currentChatOutboxReadId = ucrMsgId;
                        foreach (var m in _messageItems)
                            if (m.IsOutgoing && m.Id <= ucrMsgId)
                                m.IsRead = true;
                    }
                    break;

                case "messageThreadInfo":
                    // Ответ на getMessageThread — открываем тред
                    long threadChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long threadMsgId  = update["message_thread_id"]?.ToObject<long>() ?? 0;
                    if (threadChatId != 0 && threadMsgId != 0 && _chatsDict.ContainsKey(threadChatId)) {
                        _threadMessageId = threadMsgId;
                        _threadChatId = threadChatId;
                        OpenChat(_chatsDict[threadChatId], threadMsgId);
                    }
                    break;

                case "stickerSets":
                    HandleStickerSets(update);
                    break;

                case "stickerSet":
                    HandleStickerSet(update["sticker_set"] ?? update);
                    break;

                case "updatePoll":
                    // Обновление результатов опроса
                    var updPoll = update["poll"];
                    if (updPoll != null) {
                        long updPollId = updPoll["id"]?.ToObject<long>() ?? 0;
                        // Ищем сообщение с этим опросом в текущем чате
                        var pollMsg = _messagesDict.Values.FirstOrDefault(m => m.IsPoll && m.Id == updPollId);
                        if (pollMsg == null) {
                            // Ищем по любому совпадению — poll id может не совпадать с msg id
                            pollMsg = _messagesDict.Values.FirstOrDefault(m => m.IsPoll);
                        }
                        if (pollMsg != null) {
                            int totalVotes = updPoll["total_voter_count"]?.ToObject<int>() ?? 0;
                            var opts = updPoll["options"] as JArray;
                            if (opts != null && pollMsg.PollOptions.Count == opts.Count) {
                                for (int i = 0; i < opts.Count; i++) {
                                    int votes = opts[i]["voter_count"]?.ToObject<int>() ?? 0;
                                    int pct = totalVotes > 0 ? (int)Math.Round(votes * 100.0 / totalVotes) : 0;
                                    pollMsg.PollOptions[i].VoteCount = votes;
                                    pollMsg.PollOptions[i].Percent   = pct;
                                    pollMsg.PollOptions[i].IsChosen  = opts[i]["is_chosen"]?.ToObject<bool>() ?? false;
                                }
                            }
                        }
                    }
                    break;

                case "users":
                    var contactUserIds = update["user_ids"] as JArray;
                    if (contactUserIds != null) {
                        var uids = contactUserIds;
                        var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => {
                            try { await HandleContactsLoaded(uids); }
                            catch (Exception ex) { Log("CONTACTS ERR: " + ex.Message); }
                        });
                    }
                    break;

                case "basicGroupFullInfo":
                    if (ProfileOverlay.Visibility == Visibility.Visible) {
                        string desc2 = update["description"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(desc2)) { ProfileBio.Text = desc2; ProfileBioPanel.Visibility = Visibility.Visible; }
                        var bgMembers = update["members"] as JArray;
                        if (bgMembers != null) ShowProfileMembers(bgMembers.Select(m => m["member_id"]?["user_id"]?.ToObject<long>() ?? 0).Where(id => id != 0).ToList());
                    }
                    break;

                case "supergroupMembers":
                    if (ProfileOverlay.Visibility == Visibility.Visible) {
                        var sgMembers = update["members"] as JArray;
                        if (sgMembers != null) ShowProfileMembers(sgMembers.Select(m => m["member_id"]?["user_id"]?.ToObject<long>() ?? 0).Where(id => id != 0).ToList());
                    }
                    break;

                case "userFullInfo":
                    // Bio пользователя
                    if (ProfileOverlay.Visibility == Visibility.Visible) {
                        string bio = update["bio"]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(bio)) {
                            ProfileBio.Text = bio;
                            ProfileBioPanel.Visibility = Visibility.Visible;
                        }
                    }
                    break;

                case "supergroupFullInfo":
                    // Description группы/канала
                    if (ProfileOverlay.Visibility == Visibility.Visible) {
                        string desc = update["description"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(desc)) {
                            ProfileBio.Text = desc;
                            ProfileBioPanel.Visibility = Visibility.Visible;
                        }
                    }
                    break;

                case "ok":
                    break;

                case "storageStatistics":
                    // Ответ на optimizeStorage — TDLib реально закончил чистку файлов
                    // и обновил свою внутреннюю БД (местные пути и статусы загрузки).
                    // Только теперь безопасно считать кэш очищенным.
                    _optimizeStorageTcs?.TrySetResult(true);
                    break;

                case "updateMessageSendSucceeded": {
                    // TDLib подтвердил отправку и заменил временный (pending) id,
                    // под которым сообщение показывалось оптимистично, на
                    // постоянный. Если это не отследить, любое действие над
                    // "только что отправленным" сообщением (редактирование,
                    // ответ, закрепление, реакция) уходит с уже недействительным
                    // id и молча проваливается на сервере.
                    long sentOldId = update["old_message_id"]?.ToObject<long>() ?? 0;
                    var sentMsg = update["message"];
                    long sentNewId = sentMsg?["id"]?.ToObject<long>() ?? 0;
                    if (sentOldId != 0 && sentNewId != 0 && sentOldId != sentNewId && _messagesDict.ContainsKey(sentOldId)) {
                        var sentItem = _messagesDict[sentOldId];
                        sentItem.Id = sentNewId;
                        _messagesDict.Remove(sentOldId);
                        _messagesDict[sentNewId] = sentItem;
                        if (_pinnedMessageId == sentOldId) _pinnedMessageId = sentNewId;
                        if (_replyToMessageId == sentOldId) _replyToMessageId = sentNewId;
                        if (_editingMessageId == sentOldId) _editingMessageId = sentNewId;
                        foreach (var upKey in _uploadFileToMsgId.Keys.Where(k => _uploadFileToMsgId[k] == sentOldId).ToList())
                            _uploadFileToMsgId[upKey] = sentNewId;
                        // Те же словари fileId/remoteUniqueId → msgId, которыми пользуется
                        // обработчик завершения докачки — если вложение (стикер, фото,
                        // видео, аудио) ещё качается в момент подтверждения отправки,
                        // он должен найти сообщение уже под новым id, а не под тем,
                        // что уже удалён из _messagesDict.
                        foreach (var fk in _fileToMsgId.Keys.Where(k => _fileToMsgId[k] == sentOldId).ToList())
                            _fileToMsgId[fk] = sentNewId;
                        foreach (var vk in _videoFileIds.Keys.Where(k => _videoFileIds[k] == sentOldId).ToList())
                            _videoFileIds[vk] = sentNewId;
                        foreach (var ak in _audioFileIds.Keys.Where(k => _audioFileIds[k] == sentOldId).ToList())
                            _audioFileIds[ak] = sentNewId;
                        foreach (var ruk in _remoteUniqueIdToMsgId.Keys.Where(k => _remoteUniqueIdToMsgId[k] == sentOldId).ToList())
                            _remoteUniqueIdToMsgId[ruk] = sentNewId;
                        if (_inlinePhotoFileId.ContainsKey(sentOldId)) {
                            _inlinePhotoFileId[sentNewId] = _inlinePhotoFileId[sentOldId];
                            _inlinePhotoFileId.Remove(sentOldId);
                        }
                    }
                    break;
                }

                case "updateMessageSendFailed": {
                    // Раньше не обрабатывалось вообще: TDLib создаёт локальное
                    // "эхо" сообщения ещё до подтверждения сервера, и если сервер
                    // отказывал, отказ оставался незамеченным — сообщение висело
                    // с обычной галочкой, как будто доставлено.
                    long failOldId = update["old_message_id"]?.ToObject<long>() ?? 0;
                    var failMsg = update["message"];
                    long failNewId = failMsg?["id"]?.ToObject<long>() ?? 0;
                    // В разных версиях TDLib ошибка лежит либо вложенным объектом
                    // error, либо плоскими полями — читаем оба варианта.
                    var failErr = update["error"];
                    int failCode = failErr?["code"]?.ToObject<int>()
                                ?? update["error_code"]?.ToObject<int>() ?? 0;
                    string failText = failErr?["message"]?.ToString()
                                   ?? update["error_message"]?.ToString() ?? "";

                    MessageItem failItem = null;
                    if (failOldId != 0 && _messagesDict.ContainsKey(failOldId)) failItem = _messagesDict[failOldId];
                    else if (failNewId != 0 && _messagesDict.ContainsKey(failNewId)) failItem = _messagesDict[failNewId];

                    if (failItem != null) {
                        failItem.SendFailed = true;
                        // Неудачному сообщению TDLib тоже выдаёт новый id — переносим,
                        // чтобы дальнейшие апдейты по нему находили тот же объект.
                        if (failOldId != 0 && failNewId != 0 && failOldId != failNewId
                            && _messagesDict.ContainsKey(failOldId)) {
                            failItem.Id = failNewId;
                            _messagesDict.Remove(failOldId);
                            _messagesDict[failNewId] = failItem;
                        }
                    }

                    string failBody = failCode != 0 ? failCode + ": " + failText : failText;
                    ShowToastNotification(Loc.T("toast_send_failed"), failBody, 0);
                    break;
                }

                case "updateMessageContent":
                    long umcChatId = update["chat_id"]?.ToObject<long>() ?? 0;
                    long umcMsgId = update["message_id"]?.ToObject<long>() ?? 0;
                    if (umcChatId == _currentChatId && _messagesDict.ContainsKey(umcMsgId)) {
                        var content = update["new_content"];
                        string cType = content?["@type"]?.ToString() ?? "";
                        if (cType == "messageText") {
                            string newText = content["text"]?["text"]?.ToString() ?? "";
                            _messagesDict[umcMsgId].Text = newText;
                            // Обновляем link_preview если появился
                            var lp2 = content["link_preview"];
                            if (lp2 != null) {
                                var item2 = _messagesDict[umcMsgId];
                                item2.LinkPreviewUrl = lp2["url"]?.ToString() ?? "";
                                item2.LinkPreviewSiteName = lp2["site_name"]?.ToString() ?? "";
                                item2.LinkPreviewTitle = lp2["title"]?.ToString() ?? "";
                                string lpDesc2 = lp2["description"]?["text"]?.ToString() ?? "";
                                item2.LinkPreviewDescription = lpDesc2.Length > 200 ? lpDesc2.Substring(0, 200) + "..." : lpDesc2;
                            }
                        } else if (cType == "messagePhoto") {
                            var itemPhoto = _messagesDict[umcMsgId];
                            ApplyPhotoContent(itemPhoto, umcMsgId, content, itemPhoto.IsOutgoing);
                        }
                    }
                    break;

                case "updateMessageEdited":
                    // TDLib шлёт updateMessageEdited при редактировании — дозапрашиваем сообщение.
                    // Помечаем id как "ждём именно edit-рефреш" — ответ "message" ничем не
                    // отличается от ответа на getMessage по любой другой причине, поэтому
                    // без этой пометки метка "изменено" могла бы проставиться не только
                    // из-за настоящего редактирования.
                    long umeChat = update["chat_id"]?.ToObject<long>() ?? 0;
                    long umeMsg = update["message_id"]?.ToObject<long>() ?? 0;
                    if (umeChat == _currentChatId && _messagesDict.ContainsKey(umeMsg)) {
                        _editRefreshPendingIds.Add(umeMsg);
                        TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + umeChat + ",\"message_id\":" + umeMsg + "}");
                    }
                    break;

                case "chat":
                    long openChatId = update["id"]?.ToObject<long>() ?? 0;
                    // Ответ на восстановление удалённого чата — отдаём его
                    // штатному обработчику, чтобы не дублировать логику вставки.
                    // Восстанавливаем в список ТОЛЬКО если чат реально состоит
                    // в главном списке или архиве (order != "0") — иначе TDLib
                    // просто "знает" о чате (пересылка/реплай на чужой чат),
                    // и добавлять его в чатлист не нужно.
                    if (openChatId != 0 && _pendingRestoreChatIds.Remove(openChatId)) {
                        if (TryGetActivePosition(update, out bool restoredToArchive)) {
                            if (!_chatsDict.ContainsKey(openChatId)) {
                                var restored = new JObject {
                                    ["@type"] = "updateNewChat",
                                    ["chat"] = update.DeepClone()
                                };
                                HandleUpdate("updateNewChat", restored);   // наполняет _chatsDict
                            }
                            if (restoredToArchive) _archiveChatIds.Add(openChatId);
                            else _archiveChatIds.Remove(openChatId);
                            RestoreChatIntoList(openChatId);               // и только теперь — в список
                            MoveChatToTop(openChatId);
                        }
                    }
                    // Открыть чат по упоминанию (searchPublicChat / createPrivateChat)
                    if (_pendingOpenChat && openChatId != 0) {
                        _pendingOpenChat = false;
                        if (_chatsDict.ContainsKey(openChatId))
                            OpenChat(_chatsDict[openChatId], 0);
                        else
                            _pendingHistoryChatId = openChatId;
                    }
                    // Ответ на getChat — берём pinned_message_id
                    long getChatId = openChatId;
                    if (getChatId != 0 && getChatId == _pendingPinnedChatId) {
                        _pendingPinnedChatId = 0;
                        // TDLib 1.8+ хранит список в pinned_message_ids
                        var pinnedIds = update["pinned_message_ids"] as Newtonsoft.Json.Linq.JArray;
                        long pinnedId = pinnedIds != null && pinnedIds.Count > 0
                            ? pinnedIds[0].ToObject<long>()
                            : update["pinned_message_id"]?.ToObject<long>() ?? 0;
                        if (pinnedId != 0 && getChatId == _currentChatId) {
                            _pinnedMessageId = pinnedId;
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + getChatId + ",\"message_id\":" + pinnedId + "}");
                        }
                    }
                    // Ответ на createPrivateChat — открываем чат
                    if (_pendingContactUserId != 0) {
                        long newChatId = update["id"]?.ToObject<long>() ?? 0;
                        _pendingContactUserId = 0;
                        if (newChatId != 0) {
                            // updateNewChat придёт и добавит в _chatsDict, но может опоздать
                            // Создаём ChatItem на месте если ещё нет
                            if (!_chatsDict.ContainsKey(newChatId)) {
                                var ci = new ChatItem {
                                    Id = newChatId,
                                    Title = update["title"]?.ToString() ?? Loc.T("label_chat")
                                };
                                _chatsDict[newChatId] = ci;
                            }
                            OpenChat(_chatsDict[newChatId], 0);
                        }
                    }
                    break;

                case "message":
                    long fetchedMsgId = update["id"]?.ToObject<long>() ?? 0;
                    long fetchedChatId2 = update["chat_id"]?.ToObject<long>() ?? 0;
                    // Ответ на getChatPinnedMessage
                    // Ответ на getMessage для сервисного сообщения о закреплении
                    if (fetchedMsgId != 0 && _pinnedTextRequests.ContainsKey(fetchedMsgId)) {
                        long serviceMsgId = _pinnedTextRequests[fetchedMsgId];
                        _pinnedTextRequests.Remove(fetchedMsgId);
                        if (_messagesDict.ContainsKey(serviceMsgId)) {
                            var serviceItem = _messagesDict[serviceMsgId];
                            var pinnedContent = update["content"];
                            string pinnedType = pinnedContent?["@type"]?.ToString() ?? "";
                            string pinnedText = pinnedType == "messageText"
                                ? pinnedContent["text"]?["text"]?.ToString()
                                : pinnedType == "messagePhoto" ? "📷 " + Loc.T("media_photo")
                                : pinnedType == "messageVideo" ? "🎥 " + Loc.T("media_video")
                                : pinnedType == "messageSticker" ? pinnedContent["sticker"]?["emoji"]?.ToString()
                                : Loc.T("media_message");
                            if (!string.IsNullOrEmpty(pinnedText))
                                serviceItem.Text = serviceItem.Text + "\n«" + pinnedText.Split('\n')[0].Substring(0, Math.Min(pinnedText.Length, 50)) + "»";
                        }
                    }

                    if (_pinnedMessageId == -1 && fetchedChatId2 == _currentChatId && fetchedMsgId != 0) {
                        _pinnedMessageId = fetchedMsgId;
                        var pc = update["content"];
                        string pType = pc?["@type"]?.ToString() ?? "";
                        string pText = pType == "messageText" ? pc["text"]?["text"]?.ToString()
                            : pType == "messagePhoto" ? "📷 " + Loc.T("media_photo")
                            : pType == "messageVideo" ? "🎥 " + Loc.T("media_video")
                            : pType == "messageDocument" ? "📄 " + (pc["document"]?["file_name"]?.ToString() ?? Loc.T("media_file"))
                            : pType == "messageAudio" ? "🎵 " + Loc.T("media_audio")
                            : pType == "messageVoiceNote" ? "🎤 " + Loc.T("media_voice")
                            : pType == "messageVideoNote" ? "⏺ " + Loc.T("media_videoMessage")
                            : pType == "messageSticker" ? pc["sticker"]?["emoji"]?.ToString() + " " + Loc.T("media_sticker")
                            : Loc.T("media_message");
                        PinnedMessageText.Text = pText ?? "";
                        PinnedMessageBar.Visibility = Visibility.Visible;
                    }
                    // Ответ на getMessage — заполняем ReplyToText если ждали
                    if (fetchedMsgId != 0 && _replyRequests.ContainsKey(fetchedMsgId)) {
                        var waitingItem = _replyRequests[fetchedMsgId];
                        _replyRequests.Remove(fetchedMsgId);
                        var fc = update["content"];
                        string fType = fc?["@type"]?.ToString() ?? "";
                        string fText = fType == "messageText"
                            ? fc["text"]?["text"]?.ToString()
                            : fType == "messagePhoto" ? "📷 " + Loc.T("media_photo")
                            : fType == "messageVideo" ? "🎥 " + Loc.T("media_video")
                            : fType == "messageDocument" ? "📄 " + Loc.T("media_file")
                            : fType == "messageAudio" ? "🎵 " + Loc.T("media_audio")
                            : fType == "messageVoiceNote" ? "🎤 " + Loc.T("media_voice")
                            : Loc.T("media_message");
                        waitingItem.ReplyToText = string.IsNullOrEmpty(fText) ? Loc.T("media_message") : fText;
                    }
                    // Обновляем текст если это ответ после редактирования
                    if (fetchedMsgId != 0 && _messagesDict.ContainsKey(fetchedMsgId)) {
                        var mc = update["content"];
                        string mcType = mc?["@type"]?.ToString() ?? "";
                        bool wasEditRefresh = _editRefreshPendingIds.Remove(fetchedMsgId);
                        if (mcType == "messageText") {
                            string refreshed = mc["text"]?["text"]?.ToString() ?? "";
                            _messagesDict[fetchedMsgId].Text = refreshed;
                            if (wasEditRefresh) _messagesDict[fetchedMsgId].IsEdited = true;
                        } else if (mcType == "messagePoll") {
                            // Полная перезагрузка — заменяем MessageItem в списке целиком
                            var newItem = ParseMessage(update);
                            if (newItem != null && newItem.IsPoll) {
                                int idx = -1;
                                for (int i = 0; i < _messageItems.Count; i++)
                                    if (_messageItems[i].Id == fetchedMsgId) { idx = i; break; }
                                if (idx >= 0) {
                                    _messageItems[idx] = newItem;
                                    _messagesDict[fetchedMsgId] = newItem;
                                }
                            }
                        }
                    }
                    break;

                case "chats":
                    var chatIds = update["chat_ids"] as JArray;
                    if (chatIds != null) {
                        // Результаты поиска — если поисковый запрос активен
                        if (!string.IsNullOrEmpty(_searchQuery) && !_loadingArchiveIds && !_loadingChats && _pendingFolderLoad == 0) {
                                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                foreach (var cId in chatIds) {
                                    long id = (long)cId;
                                    if (_searchAllResults.Any(r => r.ChatId == id && r.Type == SearchResultItem.ResultType.Chat)) continue;
                                    if (!_searchAllResults.Any(r => r.IsHeader && r.Title == Loc.T("search_chats")))
                                        _searchAllResults.Insert(0, new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = Loc.T("search_chats") });
                                    // Берём данные из _chatsDict или _rawChatsDict
                                    string srTitle = _chatsDict.ContainsKey(id) ? _chatsDict[id].Title : "";
                                    BitmapImage srPhoto = _chatsDict.ContainsKey(id) ? _chatsDict[id].Photo : null;
                                    string srUsername = "";
                                    if (_rawChatsDict.ContainsKey(id)) {
                                        var raw = _rawChatsDict[id] as Newtonsoft.Json.Linq.JObject;
                                        if (string.IsNullOrEmpty(srTitle)) srTitle = raw?["title"]?.ToString() ?? "";
                                        srUsername = raw?["username"]?.ToString()
                                                  ?? raw?["usernames"]?["editable_username"]?.ToString()
                                                  ?? raw?["type"]?["username"]?.ToString()
                                                  ?? raw?["type"]?["usernames"]?["editable_username"]?.ToString()
                                                  ?? "";
                                        // Для супергрупп — ищем в _supergroupDict
                                        if (string.IsNullOrEmpty(srUsername)) {
                                            long sgId = raw?["type"]?["supergroup_id"]?.ToObject<long>() ?? 0;
                                            if (sgId != 0 && _supergroupDict.ContainsKey(sgId)) {
                                                var sg3 = _supergroupDict[sgId];
                                                srUsername = sg3["username"]?.ToString()
                                                          ?? sg3["usernames"]?["editable_username"]?.ToString() ?? "";
                                            }
                                            if (string.IsNullOrEmpty(srUsername) && sgId != 0)
                                                TdJson.SendUtf8(_client, "{\"@type\":\"getSupergroup\",\"supergroup_id\":" + sgId + "}");
                                        }
                                        // Для приватных чатов (пользователей) — берём username из _usersDict
                                        if (string.IsNullOrEmpty(srUsername)) {
                                            long uid3 = raw?["type"]?["user_id"]?.ToObject<long>() ?? 0;
                                            if (uid3 != 0 && _usersDict.ContainsKey(uid3)) {
                                                var u3 = _usersDict[uid3];
                                                srUsername = u3["username"]?.ToString()
                                                          ?? u3["usernames"]?["editable_username"]?.ToString() ?? "";
                                            }
                                        }
                                    }
                                    if (string.IsNullOrEmpty(srTitle)) continue;
                                    string srSubtitle = !string.IsNullOrEmpty(srUsername) ? "@" + srUsername : "";
                                    var srItem = new SearchResultItem {
                                        Type = SearchResultItem.ResultType.Chat,
                                        ChatId = id, Title = srTitle,
                                        Subtitle = srSubtitle, Photo = srPhoto
                                    };
                                    _searchAllResults.Add(srItem);
                                    // Если фото нет — запускаем загрузку
                                    if (srPhoto == null && _rawChatsDict.ContainsKey(id)) {
                                        var rawCh = _rawChatsDict[id] as Newtonsoft.Json.Linq.JObject;
                                        var phSmallSr = rawCh?["photo"]?["small"];
                                        if (phSmallSr != null) {
                                            long phFid = phSmallSr["id"]?.ToObject<long>() ?? 0;
                                            string phPath = phSmallSr["local"]?["path"]?.ToString();
                                            if (!string.IsNullOrEmpty(phPath))
                                                { var t2 = UpdateAvatarSearchResult(srItem, phPath); }
                                            else if (phFid > 0) {
                                                _fileToSearchResult[phFid] = srItem;
                                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + phFid + ",\"priority\":10,\"synchronous\":false}");
                                            }
                                        }
                                    }
                                }
                            });
                            break;
                        }
                        if (_loadingArchiveIds) {
                            // Pre-fetch: сохраняем id архивных чатов, потом грузим главный список
                            _loadingArchiveIds = false;
                            _archiveChatIds.Clear();
                            foreach (var cId in chatIds)
                                _archiveChatIds.Add((long)cId);
                            TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListMain\"},\"limit\":1000}");
                            _loadingChats = true;
                        } else if (_pendingFolderLoad != 0) {
                            // Чаты папки
                            int fid = _pendingFolderLoad;
                            _pendingFolderLoad = 0;
                            var folderIds = new List<long>();
                            foreach (var cId in chatIds)
                                folderIds.Add((long)cId);
                            _folderChatIds[fid] = folderIds;
                            if (_currentFolderId == fid)
                                SwitchFolder(fid);
                            LoadNextFolder(); // загружаем следующую папку
                        } else {
                            _pendingChatIds.Clear();
                            foreach (var cId in chatIds)
                                _pendingChatIds.Enqueue((long)cId);
                            if (chatIds.Count == 0 && _loadingArchive) {
                                _loadingArchive = false;
                                ArchiveChatCountText.Text = Loc.T("archive_empty");
                            }
                            LoadNextChat();
                        }
                    }
                    break;

                case "foundMessages":
                    // Ответ на searchMessages
                    if (!string.IsNullOrEmpty(_searchQuery)) {
                        var foundMsgs2 = update["messages"] as JArray;
                        if (foundMsgs2 != null && foundMsgs2.Count > 0) {
                            var ignored3 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                bool hadHdr = _searchAllResults.Any(r => r.IsHeader && r.Title == Loc.T("search_messages"));
                                foreach (var fm in foundMsgs2) {
                                    long fmChatId = fm["chat_id"]?.ToObject<long>() ?? 0;
                                    long fmMsgId  = fm["id"]?.ToObject<long>() ?? 0;
                                    string fmText = fm["content"]?["text"]?["text"]?.ToString()
                                                 ?? fm["content"]?["caption"]?["text"]?.ToString() ?? "";
                                    if (string.IsNullOrEmpty(fmText)) continue;
                                    if (_searchAllResults.Any(r => r.MessageId == fmMsgId)) continue;
                                    if (!hadHdr) {
                                        _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Divider });
                                        _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = Loc.T("search_messages") });
                                        hadHdr = true;
                                    }
                                    string chatTitle = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Title : Loc.T("label_chat");
                                    BitmapImage chatPhoto = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Photo : null;
                                    int date = fm["date"]?.ToObject<int>() ?? 0;
                                    string dateStr = date > 0 ? DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("dd.MM HH:mm") : "";
                                    _searchAllResults.Add(new SearchResultItem {
                                        Type = SearchResultItem.ResultType.Message,
                                        ChatId = fmChatId, MessageId = fmMsgId,
                                        Title = chatTitle, Subtitle = fmText,
                                        DateText = dateStr, Photo = chatPhoto
                                    });
                                }
                            });
                        }
                    }
                    break;

                case "foundChatMessages":
                    // Настоящий ответ на searchChatMessages в TDLib 1.8.66 —
                    // отдельный тип, не "messages" (это выяснилось не сразу).
                    if (_chatSearchAwaitingResults) {
                        _chatSearchAwaitingResults = false;
                        HandleChatSearchResults(update);
                    }
                    break;

                case "messages":
                    if (_chatSearchAwaitingResults) {
                        _chatSearchAwaitingResults = false;
                        HandleChatSearchResults(update);
                        break;
                    }
                    // Результаты searchMessages
                    if (!string.IsNullOrEmpty(_searchQuery) && update["total_count"] != null && _pendingHistoryChatId == 0) {
                        var foundMsgs = update["messages"] as JArray;
                        if (foundMsgs != null && foundMsgs.Count > 0) {
                            var ignored2 = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                bool hadHeader = _searchAllResults.Any(r => r.IsHeader && r.Title == Loc.T("search_messages"));
                                foreach (var fm in foundMsgs) {
                                    long fmChatId = fm["chat_id"]?.ToObject<long>() ?? 0;
                                    long fmMsgId  = fm["id"]?.ToObject<long>() ?? 0;
                                    string fmText = fm["content"]?["text"]?["text"]?.ToString()
                                                 ?? fm["content"]?["caption"]?["text"]?.ToString() ?? "";
                                    if (string.IsNullOrEmpty(fmText)) continue;
                                    if (_searchAllResults.Any(r => r.MessageId == fmMsgId)) continue;
                                    if (!hadHeader) {
                                        _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = Loc.T("search_messages") });
                                        hadHeader = true;
                                    }
                                    string chatTitle = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Title : Loc.T("label_chat");
                                    BitmapImage chatPhoto = _chatsDict.ContainsKey(fmChatId) ? _chatsDict[fmChatId].Photo : null;
                                    int date = fm["date"]?.ToObject<int>() ?? 0;
                                    string dateStr = date > 0 ? DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("dd.MM HH:mm") : "";
                                    _searchAllResults.Add(new SearchResultItem {
                                        Type = SearchResultItem.ResultType.Message,
                                        ChatId = fmChatId, MessageId = fmMsgId,
                                        Title = chatTitle, Subtitle = fmText,
                                        DateText = dateStr, Photo = chatPhoto
                                    });
                                }
                            });
                        }
                        break;
                    }
                    long expectedChat = _pendingHistoryChatId;
                    var msgs = update["messages"] as JArray;
                    int totalCount = update["total_count"]?.ToObject<int>() ?? 0;
                    if (expectedChat != _currentChatId) { Log("SKIP — user switched chat"); break; }
                    int gotCount = msgs?.Count ?? 0;

                    if (!_loadingOlderHistory) {
                        // Начальная загрузка — retry если пришло слишком мало
                        if (gotCount < 2 && _historyRetryCount < 2) {
                            _historyRetryCount++;
                            var retryChat = _currentChatId;
                            Task.Delay(800).ContinueWith(_ =>
                                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                                    if (_currentChatId == retryChat)
                                        TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + retryChat + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
                                }));
                            break;
                        }
                        _messageItems.Clear();
                        _hasMoreHistory = gotCount > 0;
                        for (int i = msgs.Count - 1; i >= 0; i--) {
                            var it = ParseMessage(msgs[i]);
                            if (it != null) _messageItems.Add(it);
                        }
                        InsertDateSeparators();
                        RecomputeAlbumGrouping();
                        // Если получили меньше 50 — дозагружаем более старые
                        if (gotCount > 0 && gotCount < 50) {
                            long oldestId = msgs[msgs.Count - 1]?["id"]?.ToObject<long>() ?? 0;
                            if (oldestId != 0) {
                                _loadingOlderHistory = true;
                                TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + expectedChat + ",\"from_message_id\":" + oldestId + ",\"offset\":0,\"limit\":" + (50 - gotCount) + "}");
                            }
                        }
                        _isLoadingHistory = false;
                        LoadingIndicator.Visibility = Visibility.Collapsed;
                        MessagesListView.Visibility = Visibility.Visible;
                        // Кнопка Старт для ботов с пустой историей
                        if (_currentChatIsBot && _messageItems.Count == 0)
                            StartBotButton.Visibility = Visibility.Visible;
                        else
                            StartBotButton.Visibility = Visibility.Collapsed;
                        if (_messageItems.Count > 0) {
                            if (_pendingScrollToMsgId != 0) {
                                // Скроллим к конкретному сообщению из поиска
                                long scrollTarget = _pendingScrollToMsgId;
                                _pendingScrollToMsgId = 0;
                                // Ждём рендера и скроллим
                                var st = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                                st.Tick += (ts, te) => {
                                    st.Stop();
                                    var target = _messageItems.FirstOrDefault(m => !m.IsSeparator && m.Id == scrollTarget);
                                    if (target != null)
                                        MessagesListView.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
                                    else
                                        MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
                                };
                                st.Start();
                            } else {
                                ScrollToBottomDelayed();
                            }
                        }
                        long lastMsgId = _messageItems.Count > 0 ? _messageItems[_messageItems.Count - 1].Id : 0;
                        if (lastMsgId != 0) {
                            TdJson.SendUtf8(_client, "{\"@type\":\"viewMessages\",\"chat_id\":" + expectedChat + ",\"message_ids\":[" + lastMsgId + "],\"force_read\":true}");
                            // Не ждём updateChatReadInbox — если чат закрыть раньше, чем
                            // подтверждение придёт от сервера, закешированный
                            // last_read_inbox_message_id в _rawChatsDict останется старым.
                            // При повторном (особенно быстром) входе в тот же чат
                            // InsertUnreadSeparator() снова найдёт "непрочитанные" и
                            // ScrollToBottomDelayed() уедет к разделителю вместо низа.
                            // Помечаем прочитанным в кэше сразу же, оптимистично.
                            if (_rawChatsDict.ContainsKey(expectedChat)) {
                                var raw = _rawChatsDict[expectedChat] as JObject;
                                if (raw != null)
                                    raw["last_read_inbox_message_id"] = lastMsgId;
                            }
                            _lastReadInboxMsgId = lastMsgId;
                        }
                    } else if (_loadingOlderHistory) {
                        // Дозагрузка старых — вставляем в начало, сохраняем позицию скролла
                        _loadingOlderHistory = false;
                        OlderLoadingIndicator.Visibility = Visibility.Collapsed;
                        OlderProgressRing.IsActive = false;
                        if (gotCount == 0) {
                            _hasMoreHistory = false;
                        } else {
                            _scrollTimer?.Stop();
                            _autoScrolling = false;
                            double oldHeight = MessagesScrollViewer.ExtentHeight;
                            double oldOffset = MessagesScrollViewer.VerticalOffset;
                            int insertIdx = 0;
                            for (int i = msgs.Count - 1; i >= 0; i--) {
                                var it = ParseMessage(msgs[i]);
                                if (it != null) _messageItems.Insert(insertIdx++, it);
                            }
                            RebuildDateSeparators();
                            RecomputeAlbumGrouping();
                            _hasMoreHistory = gotCount > 0;
                            _trimming = true;
                            double capturedOld = oldOffset;
                            double capturedOldH = oldHeight;
                            int attempts = 0;
                            var fixTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                            fixTimer.Tick += (ft, fe) => {
                                double newH = MessagesScrollViewer.ExtentHeight;
                                if (newH > capturedOldH || attempts >= 10) {
                                    fixTimer.Stop();
                                    MessagesScrollViewer.ChangeView(null, capturedOld + (newH - capturedOldH), null, true);
                                    _trimming = false;
                                }
                                attempts++;
                            };
                            fixTimer.Start();
                        }
                    }
                    break;
            }
        }

        private ScrollViewer FindScrollViewer(DependencyObject element) {
            if (element is ScrollViewer sv) return sv;
            int count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++) {
                var result = FindScrollViewer(Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
                if (result != null) return result;
            }
            return null;
        }

        private double _prevScrollOffset = 0;

        /// <summary>
        /// Появление клавиатуры. По умолчанию UWP сама паникует и сдвигает
        /// (транслирует) всю страницу целиком, чтобы сфокусированное поле
        /// ввода осталось видно — вместе с ним уезжает и шапка чата. Вместо
        /// этого подрезаем снизу Grid чата (MessagesPanel) на высоту
        /// перекрытой клавиатурой области — сжимается только строка "*"
        /// со списком сообщений, шапка (Row 0) и поле ввода (Row 3) остаются
        /// на местах. EnsuredFocusedElementInView=true отключает системный
        /// автопан поверх нашего.
        /// </summary>
        private void InputPane_Showing(Windows.UI.ViewManagement.InputPane sender, Windows.UI.ViewManagement.InputPaneVisibilityEventArgs args) {
            if (MessagesPanel.Visibility == Visibility.Visible) {
                MessagesPanel.Margin = new Thickness(0, 0, 0, args.OccludedRect.Height);
            }
            args.EnsuredFocusedElementInView = true;
        }

        private void InputPane_Hiding(Windows.UI.ViewManagement.InputPane sender, Windows.UI.ViewManagement.InputPaneVisibilityEventArgs args) {
            MessagesPanel.Margin = new Thickness(0);
            args.EnsuredFocusedElementInView = true;
        }

        private void MessagesScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) {
            double offset    = MessagesScrollViewer.VerticalOffset;
            double scrollable = MessagesScrollViewer.ScrollableHeight;
            bool atBottom = scrollable <= 0 || (scrollable - offset) < 50;

            // Если пользователь скроллит вверх вручную — останавливаем автоскролл вниз
            bool scrollingUp = offset < _prevScrollOffset;
            if (scrollingUp && _autoScrolling) {
                _scrollTimer?.Stop();
                _autoScrolling = false;
            }
            _prevScrollOffset = offset;

            if (_autoScrolling && atBottom) {
                _autoScrolling = false;
            }

            ScrollToBottomButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
            ScrollToBottomButton.Content = "↓";

            bool nearTop = offset < 50;
            if (nearTop && !_loadingOlderHistory && !_isLoadingHistory && _hasMoreHistory
                && _currentChatId != 0 && !_autoScrolling && !_trimming) {
                LoadOlderMessages();
            }
        }

        private void ScrollToBottom_Click(object sender, RoutedEventArgs e) {
            MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
        }

        private void ScrollToBottomDelayed() {
            _scrollTimer?.Stop();
            _autoScrolling = true;
            double prevExtent = -1;
            int stableTicks = 0;
            int totalTicks = 0;
            _scrollTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _scrollTimer.Tick += (s2, e2) => {
                totalTicks++;
                double sh = MessagesScrollViewer.ExtentHeight;
                if (sh > 0 && sh == prevExtent) {
                    stableTicks++;
                    if (stableTicks >= 2) {
                        _scrollTimer.Stop();
                        var unreadSep = _messageItems.FirstOrDefault(m => m.IsUnreadSeparator);
                        if (unreadSep != null) {
                            int sepIdx = _messageItems.IndexOf(unreadSep);
                            double itemH = sh / Math.Max(_messageItems.Count, 1);
                            MessagesScrollViewer.ChangeView(null, sepIdx * itemH, null, false);
                        } else {
                            MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
                        }
                    }
                } else {
                    stableTicks = 0;
                    prevExtent = sh;
                }
                if (totalTicks >= 30) {
                    _scrollTimer.Stop();
                    MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ScrollableHeight, null, false);
                    _autoScrolling = false;
                }
            };
            _scrollTimer.Start();
        }

        private void LoadOlderMessages() {
            // Берём самое старое сообщение — оно в начале списка (старые = индекс 0)
            var oldest = _messageItems.FirstOrDefault(m => !m.IsSeparator);
            if (oldest == null) return;
            _loadingOlderHistory = true;
            OlderLoadingIndicator.Visibility = Visibility.Visible;
            OlderProgressRing.IsActive = true;
            string req = _threadMessageId != 0
                ? "{\"@type\":\"getMessageThreadHistory\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + _threadMessageId + ",\"from_message_id\":" + oldest.Id + ",\"offset\":0,\"limit\":50}"
                : "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":" + oldest.Id + ",\"offset\":0,\"limit\":50}";
            TdJson.SendUtf8(_client, req);
        }

        private void UpdateChatStatus(JToken status) {
            if (status == null) { CurrentChatStatus.Text = ""; return; }
            string type = status["@type"]?.ToString();
            string text = "";
            switch (type) {
                case "userStatusOnline":
                    text = Loc.T("hdr_online");
                    CurrentChatStatus.Foreground = CB("#2AABEE");
                    break;
                case "userStatusOffline":
                    long wasOnline = status["was_online"]?.ToObject<long>() ?? 0;
                    text = wasOnline > 0 ? Loc.T("hdr_wasSeenPrefix") + FormatLastSeen(wasOnline) : Loc.T("hdr_offline");
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
                case "userStatusRecently":
                    text = Loc.T("hdr_wasSeenPrefix") + Loc.T("hdr_recently");
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
                case "userStatusLastWeek":
                    text = Loc.T("hdr_wasSeenPrefix") + Loc.T("hdr_lastWeek");
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
                case "userStatusLastMonth":
                    text = Loc.T("hdr_wasSeenPrefix") + Loc.T("hdr_lastMonth");
                    CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
                    break;
            }
            CurrentChatStatus.Text = text;
        }

        /// <summary>Culture used for date/month/weekday formatting, matching the selected app language.</summary>
        private static System.Globalization.CultureInfo LocCulture() {
            switch (Loc.Language) {
                case "ru": return new System.Globalization.CultureInfo("ru-RU");
                case "uk": return new System.Globalization.CultureInfo("uk-UA");
                case "he": return new System.Globalization.CultureInfo("he-IL");
                default:   return new System.Globalization.CultureInfo("en-US");
            }
        }

        private void LoadNextChat() {
            if (_pendingChatIds.Count == 0) {
                if (_loadingChats) {
                    _loadingChats = false;
                    _mainListLoaded = true;
                    for (int pi2 = 0; pi2 < Math.Min(_chatListItems.Count, 15); pi2++)
                    LoadNextFolder();
                }
                if (_loadingArchive) {
                    _loadingArchive = false;
                    ArchiveChatCountText.Text = _archiveChatItems.Count == 0
                        ? Loc.T("archive_empty") : Loc.T("archive_count") + _archiveChatItems.Count;
                    UpdateArchiveUnreadBadge();
                }
                return;
            }
            long nextId = _pendingChatIds.Dequeue();
            Task.Delay(100).ContinueWith(_ =>
                Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (_chatsDict.ContainsKey(nextId)) {
                        var existing = _chatsDict[nextId];
                        // Определяем список по флагу загрузки
                        if (_loadingArchive) {
                            if (!_archiveChatItems.Contains(existing)) {
                                if (existing.IsPinned) {
                                    int insertAt = 0;
                                    for (int pi = 0; pi < _archiveChatItems.Count; pi++)
                                        if (_archiveChatItems[pi].IsPinned) insertAt = pi + 1;
                                    _archiveChatItems.Insert(insertAt, existing);
                                } else {
                                    _archiveChatItems.Add(existing);
                                }
                                ArchiveChatCountText.Text = Loc.T("archive_count") + _archiveChatItems.Count;
                            }
                        } else {
                            if (!_chatListItems.Contains(existing)) {
                                if (existing.IsPinned) {
                                    int insertAt = 0;
                                    for (int pi = 0; pi < _chatListItems.Count; pi++)
                                        if (_chatListItems[pi].IsPinned) insertAt = pi + 1;
                                    _chatListItems.Insert(insertAt, existing);
                                } else {
                                    _chatListItems.Add(existing);
                                }
                                if (!_allChatItems.Contains(existing))
                                    _allChatItems.Add(existing);
                                ChatCountText.Text = _chatListItems.Count.ToString();
                            }
                        }
                    } else {
                        // Чат ещё не известен — запрашиваем, updateNewChat вызовет LoadNextChat сам
                        _pendingGetChat.Add(nextId);
                        TdJson.SendUtf8(_client, "{\"@type\":\"getChat\",\"chat_id\":" + nextId + "}");
                        return; // не вызываем LoadNextChat здесь — иначе двойной поток
                    }
                    LoadNextChat();
                }));
        }

        // Вставляет разделители дат в _messageItems (полная перестройка)
        /// <summary>
        /// "Вариант А" группировки альбомов: без реальной мозаики — просто
        /// схлопываем отступ/скругления между подряд идущими сообщениями
        /// с одинаковым media_album_id, и показываем имя/аватарку/время
        /// только у последнего фото в такой пачке. Разделители дат в списке
        /// пропускаем — они не участвуют в группировке.
        /// </summary>
        private void RecomputeAlbumGrouping() {
            MessageItem prev = null;
            foreach (var it in _messageItems) {
                if (it.IsSeparator) continue;
                bool sameAlbum = prev != null && !string.IsNullOrEmpty(it.AlbumId) && it.AlbumId != "0" && it.AlbumId == prev.AlbumId;
                it.IsFirstInGroup = !sameAlbum;
                if (sameAlbum) prev.IsLastInGroup = false;
                it.IsLastInGroup = true;
                prev = it;
            }
        }

        private void InsertDateSeparators() {
            var today = DateTime.Today;
            DateTime? lastDate = null;
            int i = 0;
            while (i < _messageItems.Count) {
                var item = _messageItems[i];
                if (item.IsSeparator) { i++; continue; }
                var msgDay = item.RawDate.Date;
                if (lastDate == null || msgDay != lastDate.Value) {
                    _messageItems.Insert(i, MakeSeparator(msgDay, today));
                    i += 2;
                } else { i++; }
                lastDate = msgDay;
            }
            InsertUnreadSeparator();
        }

        private void InsertUnreadSeparator() {
            if (_lastReadInboxMsgId <= 0) return;
            // Перевёрнутый список: новые в начале (индекс 0)
            // Разделитель вставляем перед первым сообщением у которого Id <= _lastReadInboxMsgId
            for (int i = 0; i < _messageItems.Count; i++) {
                var item = _messageItems[i];
                if (item.IsSeparator) continue;
                if (!item.IsOutgoing && item.Id > _lastReadInboxMsgId) {
                    // Это первое непрочитанное — разделитель перед ним
                    var sep = new MessageItem {
                        IsSeparator = true,
                        SeparatorLabel = Loc.T("chat_newMessages"),
                        IsUnreadSeparator = true,
                        Background = "#00000000"
                    };
                    _messageItems.Insert(i, sep);
                    return;
                }
            }
        }

        // Удаляет все разделители и вставляет заново (после дозагрузки старых сообщений)
        // Вставляет разделители дат только для диапазона [0..count] новых сообщений
        // Не трогает остальной список
        private void InsertDateSeparatorsForRange(int start, int count) {
            var today = DateTime.Today;
            // Обрабатываем только новые сообщения + первое старое для проверки границы
            int end = start + count;
            // Идём с конца диапазона к началу чтобы вставка не сбивала индексы
            DateTime? prevDay = null;
            // Узнаём день первого сообщения после нашего диапазона
            for (int i = end; i < _messageItems.Count; i++) {
                if (!_messageItems[i].IsSeparator) { prevDay = _messageItems[i].RawDate.Date; break; }
            }
            // Вставляем разделители для новых сообщений
            int i2 = end - 1;
            while (i2 >= start) {
                var item = _messageItems[i2];
                if (item.IsSeparator) { i2--; continue; }
                var day = item.RawDate.Date;
                if (prevDay == null || day != prevDay.Value) {
                    // Нужен разделитель перед следующим сообщением с другим днём
                    // Ищем следующее не-сепаратор после i2
                    bool needSep = prevDay == null || day != prevDay.Value;
                    if (needSep && i2 + 1 < _messageItems.Count) {
                        var next = _messageItems[i2 + 1];
                        if (!next.IsSeparator || !next.SeparatorLabel.Equals(MakeSeparator(day, today).SeparatorLabel))
                            _messageItems.Insert(i2 + 1, MakeSeparator(day, today));
                    }
                }
                prevDay = day;
                i2--;
            }
            // Проверяем нужен ли разделитель в самом начале
            if (_messageItems.Count > 0) {
                var first = _messageItems[0];
                if (!first.IsSeparator)
                    _messageItems.Insert(0, MakeSeparator(first.RawDate.Date, today));
            }
        }

        private void RebuildDateSeparators() {
            for (int i = _messageItems.Count - 1; i >= 0; i--)
                if (_messageItems[i].IsSeparator) _messageItems.RemoveAt(i);
            InsertDateSeparators();
        }

        private MessageItem MakeSeparator(DateTime day, DateTime today) {
            string label;
            int diff = (today - day).Days;
            if (diff == 0)       label = Loc.T("date_today");
            else if (diff == 1)  label = Loc.T("date_yesterday");
            else if (diff == 2)  label = Loc.T("date_dayBeforeYesterday");
            else if (day.Year == today.Year)
                                 label = day.ToString("d MMMM", LocCulture());
            else                 label = day.ToString("d MMMM yyyy", LocCulture());
            return new MessageItem { IsSeparator = true, SeparatorLabel = label };
        }

        // Вставляет незакреплённый чат сразу после последнего закреплённого
        // Закреплённый чат всегда вставляется в самый верх (позиция 0)
        private void InsertAfterPinned(ObservableCollection<ChatItem> list, ChatItem item) {
            if (item.IsPinned) {
                // Вставляем после других закреплённых
                int pinnedIdx = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].IsPinned) pinnedIdx = i + 1;
                list.Insert(pinnedIdx, item);
                return;
            }
            int insertAt = 0;
            for (int i = 0; i < list.Count; i++) {
                if (list[i].IsPinned) insertAt = i + 1;
            }
            list.Insert(insertAt, item);
        }

        /// <summary>
        /// Вставляет НЕ закреплённый чат на его настоящее хронологическое место
        /// (по убыванию Order, как сортирует сам TDLib), а не просто в начало
        /// незакреплённых — иначе, например, открепление всегда оставляло бы
        /// чат висеть на самом верху.
        /// </summary>
        private void InsertBySortOrder(ObservableCollection<ChatItem> list, ChatItem item) {
            int insertAt = list.Count;
            for (int i = 0; i < list.Count; i++) {
                if (list[i].IsPinned) continue; // закреплённые всегда наверху, не учитываем как границу
                if (list[i].Order < item.Order) { insertAt = i; break; }
            }
            list.Insert(insertAt, item);
        }

        /// <summary>Чаты, по которым отправлен getChat для восстановления в списке.</summary>
        private readonly HashSet<long> _pendingRestoreChatIds = new HashSet<long>();

        /// <summary>
        /// TDLib присылает updateNewChat не только для чатов из реального списка —
        /// объект чата также создаётся, когда клиент просто "узнаёт" о чате
        /// (пересланное сообщение, ответ на чужой чат, ссылка). У такого чата
        /// нет позиции ни в одном списке (order == "0" везде). Проверяем это
        /// по массиву positions, чтобы отличить настоящее членство в списке от
        /// побочного знания о чате.
        /// </summary>
        private bool TryGetActivePosition(JToken chatObj, out bool toArchive) {
            toArchive = false;
            var positions = chatObj?["positions"] as JArray;
            if (positions == null) return false;
            foreach (var p in positions) {
                string order = p["order"]?.ToString() ?? "0";
                if (order == "0") continue;
                string listType = p["list"]?["@type"]?.ToString();
                if (listType == "chatListArchive") { toArchive = true; return true; }
                if (listType == "chatListMain") { toArchive = false; return true; }
            }
            return false;
        }

        /// <summary>
        /// TDLib присылает updateNewChat один раз за сессию клиента. После
        /// удаления переписки чат исчезает из _chatsDict, и все дальнейшие
        /// updateChatLastMessage / updateChatPosition по нему отбрасываются —
        /// чат уже не возвращается в список до перезапуска. Дозапрашиваем чат
        /// и прогоняем ответ через обычный путь updateNewChat.
        ///
        /// Важно: даже если чат уже есть в _chatsDict, это ещё не значит, что
        /// он реально состоит в списке — он мог попасть туда фоново (пересылка,
        /// реплай на чужой чат). Поэтому всегда перепроверяем актуальную
        /// позицию через getChat, а не доверяем самому факту наличия в словаре.
        /// </summary>
        private void EnsureChatInList(long chatId) {
            if (chatId == 0) return;
            if (_loadingChats || _loadingArchive || _loadingArchiveIds) return;

            bool visible = _chatListItems.Any(c => c.Id == chatId)
                        || _archiveChatItems.Any(c => c.Id == chatId);
            if (visible) return;

            if (!_pendingRestoreChatIds.Add(chatId)) return;      // запрос уже в пути
            TdJson.SendUtf8(_client, "{\"@type\":\"getChat\",\"chat_id\":" + chatId + "}");
        }

        /// <summary>
        /// updateNewChat заполняет только _chatsDict — в видимый список чаты
        /// попадают исключительно через LoadNextChat. Для восстановленного чата
        /// этой очереди уже нет, поэтому вставляем сами.
        /// </summary>
        private void RestoreChatIntoList(long chatId) {
            if (!_chatsDict.ContainsKey(chatId)) return;
            var item = _chatsDict[chatId];
            bool toArchive = _archiveChatIds.Contains(chatId);
            var list = toArchive ? _archiveChatItems : _chatListItems;
            if (list.Any(c => c.Id == chatId)) return;

            if (item.IsPinned) InsertAfterPinned(list, item);
            else InsertBySortOrder(list, item);
            if (toArchive) {
                UpdateArchiveUnreadBadge();
            } else {
                if (!_allChatItems.Any(c => c.Id == chatId)) _allChatItems.Add(item);
                ChatCountText.Text = _chatListItems.Count.ToString();
            }
        }

        private void MoveChatToTop(long chatId) {
            var list = _inArchive ? _archiveChatItems : _chatListItems;
            var item = list.FirstOrDefault(c => c.Id == chatId);
            if (item == null) return;
            // Закреплённые не двигаем — они всегда наверху
            if (item.IsPinned) return;
            // Уже на правильной позиции (сразу после закреплённых)?
            int pinnedCount = list.Count(c => c.IsPinned);
            if (list.IndexOf(item) == pinnedCount) return;
            list.Remove(item);
            InsertAfterPinned(list, item);
        }

        private long _serverTimeOffset = 0;
        private bool _serverTimeOffsetSet = false;

        private void UpdateServerTimeOffset(long serverUnix) {
            if (_serverTimeOffsetSet) return; // устанавливаем только один раз
            long localUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _serverTimeOffset = serverUnix - localUnix;
            _serverTimeOffsetSet = true;
        }

        private long LocalUnixNow() {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _serverTimeOffset;
        }

        private string FormatLastSeen(long unixTime) {
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long diffSec = nowUnix - unixTime;
            if (diffSec < 0) diffSec = 0;
            if (diffSec < 60) return Loc.T("just_now");
            if (diffSec < 3600) return (diffSec / 60) + Loc.T("minutes_ago");
            var dtLocal = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
            var nowLocal = DateTimeOffset.UtcNow.ToLocalTime().DateTime;
            if (dtLocal.Day == nowLocal.Day) return Loc.T("lastseen_today") + dtLocal.ToString("HH:mm");
            if (dtLocal.Day == nowLocal.AddDays(-1).Day) return Loc.T("lastseen_yesterday") + dtLocal.ToString("HH:mm");
            return dtLocal.ToString("d MMM", LocCulture()) + ", " + dtLocal.ToString("HH:mm");
        }

        private string FormatCallDuration(int seconds) {
            if (seconds < 60) return seconds + Loc.T("unit_sec");
            int m = seconds / 60, s = seconds % 60;
            return m + ":" + s.ToString("D2");
        }

        private string FormatFileSize(long bytes) {
            if (bytes <= 0) return "";
            if (bytes < 1024) return bytes + Loc.T("unit_bytes");
            if (bytes < 1024 * 1024) return (bytes / 1024) + Loc.T("unit_kb");
            if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)) + Loc.T("unit_mb");
            return (bytes / (1024 * 1024 * 1024)) + Loc.T("unit_gb");
        }

        /// <summary>
        /// messageRichMessage (Bot API 10.1, июнь 2026) — контент устроен как
        /// дерево типизированных блоков (rich_message.blocks), а не единый
        /// текст/подпись. Точная TDLib-схема на момент написания нигде не
        /// задокументирована, поэтому это осторожная, best-effort попытка
        /// вытащить читаемый текст из простых текстовых блоков — заголовки,
        /// параграфы, списки, цитаты, сноски, преформатированный текст.
        /// Таблицы/карты/коллажи/слайдшоу/формулы/медиа — полноценно не
        /// рендерим (это отдельная, большая задача), просто пропускаем их
        /// при сборке текста. Все обращения через "?." — если названия полей
        /// в реальном TDLib отличаются, тут просто ничего не найдётся и
        /// сработает Loc.T("media_richMessage") в вызывающем коде.
        /// </summary>
        private static string TruncateForLog(string s) {
            if (string.IsNullOrEmpty(s)) return "(null)";
            return s.Length > 3000 ? s.Substring(0, 3000) + "…(truncated)" : s;
        }

        /// <summary>
        /// Рекурсивно вытаскивает обычный текст из дерева RichText — того же
        /// типа, что TDLib давно использует для Instant View страниц:
        /// richTextPlain — лист с текстом; richTextBold/Italic/Underline/... —
        /// обёртка вокруг вложенного RichText; richTexts — список из
        /// нескольких кусков подряд (обычный параграф почти всегда именно
        /// такой список, а не голый richTextPlain).
        /// </summary>
        private string ExtractPlainFromRichText(JToken rt) {
            if (rt == null) return "";
            string rtType = rt["@type"]?.ToString() ?? "";
            switch (rtType) {
                case "richTextPlain":
                    return rt["text"]?.ToString() ?? "";
                case "richTexts": {
                    var texts = rt["texts"] as JArray;
                    if (texts == null) return "";
                    var sb = new System.Text.StringBuilder();
                    foreach (var t in texts) sb.Append(ExtractPlainFromRichText(t));
                    return sb.ToString();
                }
                case "richTextBold":
                case "richTextItalic":
                case "richTextUnderline":
                case "richTextStrikethrough":
                case "richTextFixed":
                case "richTextMarked":
                case "richTextSubscript":
                case "richTextSuperscript":
                case "richTextUrl":
                case "richTextAnchorLink":
                    return ExtractPlainFromRichText(rt["text"]);
                case "richTextEmailAddress":
                case "richTextPhoneNumber":
                    return rt["text"]?["text"]?.ToString() ?? ExtractPlainFromRichText(rt["text"]);
                case "richTextIcon":
                    return ""; // картинка-иконка, текста нет
                default:
                    // Незнакомый вариант RichText — пробуем вложенный text, если есть
                    if (rt["text"] != null) return ExtractPlainFromRichText(rt["text"]);
                    return rt.Type == Newtonsoft.Json.Linq.JTokenType.String ? rt.ToString() : "";
            }
        }

        /// <summary>Текст одного блока (pageBlockXxx) — с рекурсией для списков/сворачиваемых секций.</summary>
        private string ExtractPageBlockText(JToken block) {
            string bType = block?["@type"]?.ToString() ?? "";
            switch (bType) {
                case "pageBlockTitle":
                case "pageBlockSubtitle":
                case "pageBlockHeader":
                case "pageBlockSubheader":
                case "pageBlockKicker":
                case "pageBlockParagraph":
                case "pageBlockPreformatted":
                case "pageBlockFooter":
                case "pageBlockBlockQuote":
                    return ExtractPlainFromRichText(block["text"]);

                case "pageBlockPullQuote": {
                    string quote = ExtractPlainFromRichText(block["text"]);
                    string credit = ExtractPlainFromRichText(block["credit"]);
                    return string.IsNullOrEmpty(credit) ? quote : quote + "\n— " + credit;
                }

                case "pageBlockList": {
                    var items = block["items"] as JArray;
                    if (items == null) return "";
                    var parts = new List<string>();
                    foreach (var it in items) {
                        string label = ExtractPlainFromRichText(it?["label"]);
                        var nested = it?["page_blocks"] as JArray;
                        string nestedText = "";
                        if (nested != null) {
                            var nb = new List<string>();
                            foreach (var nBlock in nested) {
                                string t = ExtractPageBlockText(nBlock);
                                if (!string.IsNullOrEmpty(t)) nb.Add(t);
                            }
                            nestedText = string.Join(" ", nb);
                        }
                        string line = (string.IsNullOrEmpty(label) ? "•" : label) + " " + nestedText;
                        if (!string.IsNullOrWhiteSpace(line)) parts.Add(line.Trim());
                    }
                    return string.Join("\n", parts);
                }

                case "pageBlockDetails": {
                    string header = ExtractPlainFromRichText(block["header"]);
                    var nested = block["page_blocks"] as JArray;
                    string nestedText = "";
                    if (nested != null) {
                        var nb = new List<string>();
                        foreach (var nBlock in nested) {
                            string t = ExtractPageBlockText(nBlock);
                            if (!string.IsNullOrEmpty(t)) nb.Add(t);
                        }
                        nestedText = string.Join("\n", nb);
                    }
                    return string.IsNullOrEmpty(nestedText) ? header : header + "\n" + nestedText;
                }

                case "pageBlockTable": {
                    var rows = block["cells"] as JArray;
                    if (rows == null) return "";
                    var lines = new List<string>();
                    foreach (var row in rows) {
                        var rowArr = row as JArray;
                        if (rowArr == null) continue;
                        var cellTexts = new List<string>();
                        foreach (var cell in rowArr)
                            cellTexts.Add(ExtractPlainFromRichText(cell?["text"]));
                        lines.Add(string.Join(" | ", cellTexts));
                    }
                    return string.Join("\n", lines);
                }

                // pageBlockMap/pageBlockCollage/pageBlockSlideshow/pageBlockAnimation/
                // pageBlockAudio/pageBlockPhoto/pageBlockVideo/pageBlockVoiceNote/
                // pageBlockCover/pageBlockEmbedded(Post)/pageBlockDivider/pageBlockAnchor/
                // pageBlockChatLink/pageBlockRelatedArticles — не текстовые по сути,
                // полноценно не рендерим, текст не добавляем.
                default:
                    return "";
            }
        }

        private string ExtractRichMessageText(JToken content) {
            try {
                // Реальная структура (проверено по логу): content.message.blocks,
                // а не content.blocks и не content.rich_message.blocks, как
                // предполагалось раньше.
                var blocks = content?["message"]?["blocks"] as JArray;
                if (blocks == null || blocks.Count == 0) {
                    Log("RICHMSG RAW (no blocks found): " + TruncateForLog(content?.ToString(Newtonsoft.Json.Formatting.None)));
                    return "";
                }
                var sb = new System.Text.StringBuilder();
                foreach (var block in blocks) {
                    string piece = ExtractPageBlockText(block);
                    if (!string.IsNullOrEmpty(piece)) {
                        if (sb.Length > 0) sb.Append("\n");
                        sb.Append(piece);
                    }
                }
                string result = sb.ToString();
                if (string.IsNullOrEmpty(result)) {
                    // Блоки нашлись, но ни один не дал текста — значит, среди них
                    // только нетекстовые (медиа/карта/т.п.) либо новый, ещё не
                    // учтённый тип pageBlock.
                    Log("RICHMSG RAW (blocks found, no text extracted): " + TruncateForLog(content?.ToString(Newtonsoft.Json.Formatting.None)));
                }
                return result;
            } catch (Exception ex) {
                Log("RICHMSG ERR: " + ex.Message);
                return "";
            }
        }

        private void FillChatLastMessage(ChatItem item, JToken msg, JToken chatOrUpdate) {
            try {
                var content = msg["content"];
                string mtype = content?["@type"]?.ToString() ?? "";
                string text = mtype == "messageText"
                    ? content["text"]?["text"]?.ToString() ?? ""
                    : mtype == "messagePhoto" ? "📷 " + Loc.T("media_photo")
                    : mtype == "messageVideo" && (content["video"]?["is_animation"]?.ToObject<bool>() ?? false) ? "🎞 GIF"
                    : mtype == "messageVideo" ? "🎥 " + Loc.T("media_video")
                    : mtype == "messageVoiceNote" ? "🎤 " + Loc.T("media_voice")
                    : mtype == "messageVideoNote" ? "⏺ " + Loc.T("media_videoMessage")
                    : mtype == "messageSticker" ? Loc.T("media_sticker")
                    : mtype == "messagePoll" ? "📊 " + Loc.T("media_poll")
                    : mtype == "messageDocument" ? "📄 " + Loc.T("media_document")
                    : mtype == "messageAnimation" ? "🎞 GIF"
                    : mtype == "messageCall" ? ((content["is_video"]?.ToObject<bool>() ?? false) ? "📹" : "📞") + " " + Loc.T("media_call")
                    : mtype == "messageAudio" ? "🎵 " + Loc.T("media_audio")
                    : mtype == "messagePinMessage" ? "📌 " + Loc.T("svc_pinnedMessageEvent")
                    : mtype == "messageChatAddMembers" ? "➕ " + Loc.T("svc_memberAdded")
                    : mtype == "messageChatJoinByLink" ? "➕ " + Loc.T("svc_joinedByLink")
                    : mtype == "messageChatDeleteMember" ? "➖ " + Loc.T("svc_memberLeft")
                    : mtype == "messageChatChangeTitle" ? "✏ " + Loc.T("svc_titleChanged")
                    : mtype == "messageChatChangePhoto" ? "🖼 " + Loc.T("svc_photoChanged")
                    : mtype == "messageContactRegistered" ? "👤 " + Loc.T("svc_contactRegistered")
                    : mtype == "messageLocation" ? "📍 " + Loc.T("svc_location")
                    : mtype == "messageContact" ? "👤 " + Loc.T("svc_contact")
                    : mtype == "messageRichMessage"
                        ? (string.IsNullOrEmpty(ExtractRichMessageText(content)) ? Loc.T("media_richMessage") : ExtractRichMessageText(content))
                    : "[" + mtype.Replace("message", "") + "]";
                item.LastMessage = text;
                long date = msg["date"]?.ToObject<long>() ?? 0;
                if (date > 0)
                    item.LastMessageTime = DateTimeOffset.FromUnixTimeSeconds(date).LocalDateTime.ToString("HH:mm");
                item.IsOutgoing = msg["is_outgoing"]?.ToObject<bool>() ?? false;
                long msgId = msg["id"]?.ToObject<long>() ?? 0;
                long readOutbox = chatOrUpdate["last_read_outbox_message_id"]?.ToObject<long>() ?? -1;
                if (readOutbox > 0) item.OutboxReadId = readOutbox;
                item.IsRead = item.IsOutgoing && item.OutboxReadId > 0 && item.OutboxReadId >= msgId;

                // Регистрируем upload для исходящих сообщений в состоянии pending
                if (item.IsOutgoing && msg["sending_state"]?["@type"]?.ToString() == "messageSendingStatePending") {
                    RegisterUploadTracking(msg, msgId);
                }
            } catch { }
        }

        // Регистрируем file_id для отслеживания прогресса upload
        private void RegisterUploadTracking(JToken msg, long msgId) {
            try {
                var content = msg["content"];
                string type = content?["@type"]?.ToString() ?? "";
                JToken fileToken = null;
                if (type == "messagePhoto") {
                    var sizes = content["photo"]?["sizes"] as JArray;
                    if (sizes != null && sizes.Count > 0)
                        fileToken = sizes[sizes.Count - 1]["photo"];
                } else if (type == "messageDocument")
                    fileToken = content["document"]?["document"];
                else if (type == "messageVideo")
                    fileToken = content["video"]?["video"];
                else if (type == "messageAudio")
                    fileToken = content["audio"]?["audio"];
                else if (type == "messageVoiceNote")
                    fileToken = content["voice_note"]?["voice"];
                else if (type == "messageVideoNote")
                    fileToken = content["video_note"]?["video"];

                if (fileToken != null) {
                    long fid = fileToken["id"]?.ToObject<long>() ?? 0;
                    if (fid != 0) {
                        _uploadFileToMsgId[fid] = msgId;
                        if (_messagesDict.ContainsKey(msgId))
                            _messagesDict[msgId].DownloadStatus = "⬆ 0%";
                    }
                }
            } catch { }
        }

        // Цвета для ников (по user_id % количество цветов)
        private static readonly string[] _senderColors = {
            "#E17076", "#7EC8E3", "#A695E7", "#76C99F",
            "#F2C94C", "#F78C6C", "#67D7CC", "#FF8A65"
        };

        private string GetSenderName(JToken senderId) {
            if (senderId == null) return "";
            string sType = senderId["@type"]?.ToString();
            if (sType == "messageSenderUser") {
                long uid = senderId["user_id"]?.ToObject<long>() ?? 0;
                if (_usersDict.ContainsKey(uid)) {
                    var u = _usersDict[uid];
                    string fn = u["first_name"]?.ToString() ?? "";
                    string ln = u["last_name"]?.ToString() ?? "";
                    return (fn + " " + ln).Trim();
                }
                return "User " + uid;
            }
            if (sType == "messageSenderChat") {
                long cid = senderId["chat_id"]?.ToObject<long>() ?? 0;
                if (_chatsDict.ContainsKey(cid)) return _chatsDict[cid].Title;
                return "Chat " + cid;
            }
            return "";
        }

        private string GetSenderColor(JToken senderId) {
            if (senderId == null) return _senderColors[0];
            long id = senderId["user_id"]?.ToObject<long>()
                   ?? senderId["chat_id"]?.ToObject<long>() ?? 0;
            return _senderColors[Math.Abs((int)(id % _senderColors.Length))];
        }

        /// <summary>
        /// Общая логика выбора и загрузки превью фото — используется и при
        /// первом разборе сообщения (ParseMessage), и при апдейте
        /// updateMessageContent. TDLib присылает финальный контент с полным
        /// набором размеров отдельным апдейтом уже после исходного
        /// pending-эха у только что отправленных сообщений — без повторного
        /// применения тут фото могло вообще не появиться сразу после отправки.
        /// </summary>
        private void ApplyPhotoContent(MessageItem item, long msgId, JToken content, bool outgoing) {
            var sizes = content?["photo"]?["sizes"] as JArray;
            if (sizes == null || sizes.Count == 0) return;

            // Оригинал — только для полноэкранного просмотра/сохранения,
            // тут экономить не нужно.
            var origToken = sizes[sizes.Count - 1]["photo"] as JObject;
            long origFid = origToken != null ? (long)origToken["id"] : 0;
            if (origFid != 0) {
                item.FullPhotoFileId = origFid;
                // Нужно, чтобы сработал ShowFullPhoto по завершении скачивания оригинала.
                _fileToMsgId[origFid] = msgId;
            }

            // А для превью в самом пузыре берём размер, близкий к тому,
            // что реально показывается (см. MessageItem.PhotoMaxWidth = 250),
            // а не оригинал в полном разрешении камеры.
            const int targetPhotoWidth = 600;
            int bestIdx = sizes.Count - 1;
            int bestDiff = int.MaxValue;
            for (int si = 0; si < sizes.Count; si++) {
                int w = sizes[si]["width"]?.ToObject<int>() ?? 0;
                if (w <= 0) continue;
                int diff = Math.Abs(w - targetPhotoWidth);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = si; }
            }
            var fileToken = sizes[bestIdx]["photo"] as JObject;
            if (fileToken == null) return;

            long pfid = (long)fileToken["id"];
            _inlinePhotoFileId[msgId] = pfid; // какой fid считается "превью" для этого сообщения
            _fileToMsgId[pfid] = msgId;
            _messagesDict[msgId] = item;
            bool isUploaded = fileToken["remote"]?["is_uploading_completed"]?.ToObject<bool>() ?? false;
            string phPath = fileToken["local"]?["path"]?.ToString();
            if (outgoing && !isUploaded) {
                _uploadFileToMsgId[pfid] = msgId;
                long alreadyUpl = fileToken["remote"]?["uploaded_size"]?.ToObject<long>() ?? 0;
                long phTotal = fileToken["expected_size"]?.ToObject<long>() ?? fileToken["size"]?.ToObject<long>() ?? 0;
                item.DownloadStatus = (phTotal > 0 && alreadyUpl > 0)
                    ? "⬆ " + (int)(alreadyUpl * 100 / phTotal) + "%" : "⬆ 0%";
            }
            if (!string.IsNullOrEmpty(phPath)) {
                var t = UpdateMessagePhoto(msgId, phPath);
            } else if (isUploaded || !outgoing) {
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + pfid + ",\"priority\":10,\"synchronous\":false}");
            }
        }

        private MessageItem ParseMessage(JToken msg, bool trustEditDate = true) {
            try {
                long msgId = (long)msg["id"];
                var content = msg["content"];
                string type = content["@type"]?.ToString();
                string txt = type == "messageText"
                    ? content["text"]?["text"]?.ToString() ?? ""
                    : content["caption"]?["text"]?.ToString() ?? "";

                // Парсим entities для ссылок и упоминаний
                var entitiesJson = type == "messageText"
                    ? content["text"]?["entities"] as Newtonsoft.Json.Linq.JArray
                    : content["caption"]?["entities"] as Newtonsoft.Json.Linq.JArray;
                var entities = new List<MessageEntity>();
                if (entitiesJson != null) {
                    foreach (var ent in entitiesJson) {
                        string eType = ent["type"]?["@type"]?.ToString() ?? "";
                        int offset = ent["offset"]?.ToObject<int>() ?? 0;
                        int length = ent["length"]?.ToObject<int>() ?? 0;
                        string url = null;
                        string mention = null;
                        if (eType == "textEntityTypeUrl")
                            url = txt.Substring(Math.Max(0, offset), Math.Min(length, txt.Length - offset));
                        else if (eType == "textEntityTypeTextUrl")
                            url = ent["type"]?["url"]?.ToString();
                        else if (eType == "textEntityTypeMention" && txt.Length >= offset + length)
                            mention = txt.Substring(offset, length); // @username
                        else if (eType == "textEntityTypeMentionName")
                            mention = "@id" + (ent["type"]?["user_id"]?.ToString() ?? "");
                        if (url != null) entities.Add(new MessageEntity { Offset = offset, Length = length, Url = url });
                        if (mention != null) entities.Add(new MessageEntity { Offset = offset, Length = length, Mention = mention });
                    }
                }

                bool outgoing = (bool)msg["is_outgoing"];
                var senderId = msg["sender_id"];
                var msgDate = DateTimeOffset.FromUnixTimeSeconds((long)msg["date"]).LocalDateTime;
                var item = new MessageItem {
                    Id = msgId, Text = txt,
                    Entities = entities.Count > 0 ? entities : null,
                    RawDate = msgDate,
                    Date = msgDate.ToString("HH:mm"),
                    Alignment = outgoing ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Background = outgoing ? BubbleColorOut : BubbleColorIn,
                    IsOutgoing = outgoing,
                    IsRead = outgoing && (msg["id"]?.ToObject<long>() ?? 0) <= _currentChatOutboxReadId,
                    SenderName = outgoing ? "" : GetSenderName(senderId),
                    IsGroupChat = _currentChatIsGroup,
                    IsChannelChat = _currentChatIsChannel,
                    SenderColor = GetSenderColor(senderId),
                    AlbumId = msg["media_album_id"]?.ToString() ?? "",
                    // Проверка через sending_state оказалась ненадёжной — у только
                    // что созданных сообщений (пришедших через updateNewMessage)
                    // TDLib в некоторых случаях уже отдаёт ненулевой edit_date,
                    // хотя редактирования не было. Сообщение, которое мы видим
                    // первый раз (не из истории), физически не может быть уже
                    // отредактированным — поэтому для свежих апдейтов polностью
                    // игнорируем edit_date и полагаемся только на живой
                    // updateMessageEdited/своё редактирование. Истории доверяем.
                    IsEdited = trustEditDate && (msg["edit_date"]?.ToObject<long>() ?? 0) > 0
                };

                // Аватарка отправителя — для всех входящих сообщений (и в группах,
                // и в личке — там это всегда один и тот же собеседник)
                if (!outgoing && senderId?["@type"]?.ToString() == "messageSenderUser") {
                    long senderUid = senderId["user_id"]?.ToObject<long>() ?? 0;
                    item.SenderUserId = senderUid;
                    if (_senderAvatarCache.ContainsKey(senderUid)) item.SenderPhoto = _senderAvatarCache[senderUid];
                    else EnsureSenderAvatar(senderUid);
                }

                var replyTo = msg["reply_to"];
                // Парсим link_preview для messageText
                if (type == "messageText") {
                    var lp = content["link_preview"];
                    if (lp != null) {
                        string lpSite = lp["site_name"]?.ToString() ?? "";
                        string lpTitle = lp["title"]?.ToString() ?? "";
                        string lpDesc = lp["description"]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(lpTitle) || !string.IsNullOrEmpty(lpDesc) || !string.IsNullOrEmpty(lpSite)) {
                            item.LinkPreviewUrl = lp["url"]?.ToString() ?? "";
                            item.LinkPreviewSiteName = lpSite;
                            item.LinkPreviewTitle = lpTitle;
                            item.LinkPreviewDescription = lpDesc.Length > 200 ? lpDesc.Substring(0, 200) + "..." : lpDesc;
                        }
                    }
                }
                if (replyTo != null && replyTo["@type"]?.ToString() == "messageReplyToMessage") {
                    // Автор цитаты
                    var replyOrigin = replyTo["origin"];
                    if (replyOrigin != null) {
                        string oType = replyOrigin["@type"]?.ToString();
                        if (oType == "messageOriginUser") {
                            long oUid = replyOrigin["sender_user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(oUid)) {
                                var u = _usersDict[oUid];
                                item.ReplyAuthor = (u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim();
                            }
                        } else if (oType == "messageOriginChat" || oType == "messageOriginChannel") {
                            long oCid = replyOrigin["sender_chat_id"]?.ToObject<long>() ?? 0;
                            if (_chatsDict.ContainsKey(oCid)) item.ReplyAuthor = _chatsDict[oCid].Title;
                        }
                    }
                    // Текст цитаты — сначала quote (выделенный фрагмент), потом content
                    // quote.text — это formattedText объект, поэтому нужно ["text"]["text"]
                    var quoteObj = replyTo["quote"]?["text"];
                    string replyText = quoteObj?["text"]?.ToString()  // formattedText.text
                                    ?? quoteObj?.ToString();           // fallback если вдруг строка
                    if (string.IsNullOrEmpty(replyText)) {
                        var replyContent = replyTo["content"];
                        if (replyContent != null) {
                            string rType = replyContent["@type"]?.ToString();
                            replyText = rType == "messageText"
                                ? replyContent["text"]?["text"]?.ToString()
                                : rType == "messagePhoto" ? "📷 " + Loc.T("media_photo")
                                : rType == "messageVideo" ? "🎥 " + Loc.T("media_video")
                                : rType == "messageDocument" ? "📄 " + Loc.T("media_file")
                                : rType == "messageAudio" ? "🎵 " + Loc.T("media_audio")
                                : rType == "messageVoiceNote" ? "🎤 " + Loc.T("media_voice")
                                : null;
                        }
                    }
                    item.ReplyToText = string.IsNullOrEmpty(replyText) ? "…" : replyText;
                    // Если текст не получили — запрашиваем сообщение явно
                    if (string.IsNullOrEmpty(replyText)) {
                        long replyMsgId = replyTo["message_id"]?.ToObject<long>() ?? 0;
                        long replyChatId = replyTo["chat_id"]?.ToObject<long>() ?? 0;
                        if (replyChatId == 0) replyChatId = (long)msg["chat_id"];
                        if (replyMsgId != 0) {
                            _replyRequests[replyMsgId] = item;
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + replyChatId + ",\"message_id\":" + replyMsgId + "}");
                        }
                    }
                }

                // Пересланное сообщение — извлекаем имя оригинального отправителя
                var fwdInfo = msg["forward_info"];
                if (fwdInfo != null) {
                    var origin = fwdInfo["origin"];
                    if (origin != null) {
                        string oType = origin["@type"]?.ToString();
                        if (oType == "messageOriginUser") {
                            long oUid = origin["sender_user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(oUid)) {
                                var u = _usersDict[oUid];
                                item.ForwardedFrom = (u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim();
                            } else {
                                item.ForwardedFrom = Loc.T("label_unknownUser");
                            }
                        } else if (oType == "messageOriginHiddenUser") {
                            item.ForwardedFrom = origin["sender_name"]?.ToString() ?? Loc.T("label_hiddenUser");
                        } else if (oType == "messageOriginChat") {
                            long oCid = origin["sender_chat_id"]?.ToObject<long>() ?? 0;
                            item.ForwardedFrom = _chatsDict.ContainsKey(oCid)
                                ? _chatsDict[oCid].Title
                                : origin["author_signature"]?.ToString() ?? Loc.T("label_chat");
                        } else if (oType == "messageOriginChannel") {
                            long oCid = origin["chat_id"]?.ToObject<long>() ?? 0;
                            string sig = origin["author_signature"]?.ToString();
                            string chanName = _chatsDict.ContainsKey(oCid) ? _chatsDict[oCid].Title : Loc.T("label_channel");
                            item.ForwardedFrom = string.IsNullOrEmpty(sig) ? chanName : chanName + " (" + sig + ")";
                        }
                    }
                }

                // Реакции
                var reactions = msg["interaction_info"]?["reactions"]?["reactions"] as JArray;
                if (reactions != null && reactions.Count > 0)
                    item.Reactions = BuildReactionsString(reactions);

                // Комментарии к постам канала
                var replyInfo = msg["interaction_info"]?["reply_info"];
                if (replyInfo != null) {
                    int replyCount = replyInfo["reply_count"]?.ToObject<int>() ?? 0;
                    item.ReplyCount = replyCount;
                }

                // Inline-кнопки
                var markup = msg["reply_markup"];
                if (markup != null && markup["@type"]?.ToString() == "replyMarkupInlineKeyboard") {
                    var rows = markup["rows"] as JArray;
                    if (rows != null) {
                        var buttonRows = new System.Collections.ObjectModel.ObservableCollection<InlineButtonRow>();
                        foreach (var row in rows) {
                            var btnRow = new InlineButtonRow();
                            foreach (var btn in row as JArray ?? new JArray()) {
                                string bType = btn["type"]?["@type"]?.ToString() ?? "";
                                btnRow.Buttons.Add(new InlineButton {
                                    Text = btn["text"]?.ToString() ?? "",
                                    CallbackData = bType == "inlineKeyboardButtonTypeCallback"
                                        ? btn["type"]?["data"]?.ToString() : null,
                                    Url = bType == "inlineKeyboardButtonTypeUrl"
                                        ? btn["type"]?["url"]?.ToString() : null,
                                });
                            }
                            if (btnRow.Buttons.Count > 0) buttonRows.Add(btnRow);
                        }
                        item.InlineButtons = buttonRows;
                    }
                }

                if (type == "messagePhoto") {
                    ApplyPhotoContent(item, msgId, content, outgoing);
                } else if (type == "messageVideo") {
                    bool isAnim = content["video"]?["is_animation"]?.ToObject<bool>() ?? false;
                    item.IsVideo = !isAnim;
                    item.IsGif = isAnim;
                    if (isAnim) item.Text = "";
                    var videoFile = content["video"]?["video"] as JObject;
                    var thumb = content["video"]?["thumbnail"]?["file"] as JObject;
                    if (videoFile != null) {
                        long vfid = (long)videoFile["id"];
                        _fileToMsgId[vfid] = msgId;
                        _videoFileIds[vfid] = msgId;
                        _messagesDict[msgId] = item;
                        string vPath = videoFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vPath)) {
                            if (isAnim) item.GifSource = new Uri(vPath);
                            else item.FilePath = vPath;
                        }
                    }
                    if (thumb != null) {
                        long tfid = (long)thumb["id"];
                        string tPath = thumb["local"]?["path"]?.ToString();
                        bool isImgThumb = !string.IsNullOrEmpty(tPath) &&
                            (tPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                             tPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                        if (isImgThumb && !isAnim) {
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            var t = UpdateMessagePhoto(msgId, tPath);
                        } else if (!isImgThumb && !isAnim) {
                            _fileToMsgId[tfid] = msgId;
                            _messagesDict[msgId] = item;
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                        // Для GIF тумбнейл не нужен — грузим сразу сам файл
                    }
                } else if (type == "messageAnimation") {
                    item.IsGif = true;
                    item.IsVideo = false;
                    var animFile = content["animation"]?["animation"] as JObject;
                    string animCaption = content["caption"]?["text"]?.ToString() ?? "";
                    item.Text = animCaption; // пустой если нет подписи
                    if (animFile != null) {
                        long afid = (long)animFile["id"];
                        _fileToMsgId[afid] = msgId;
                        _videoFileIds[afid] = msgId;
                        _messagesDict[msgId] = item;
                        string aPath = animFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(aPath))
                            item.GifSource = new Uri(aPath);
                        else {
                            item.VideoDownloadProgress = "⏳ 0%";
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + afid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                    // Тумбнейл для GIF не нужен — MediaElement покажет сам файл
                } else if (type == "messageSticker") {
                    var sticker = content["sticker"];
                    // is_animated/is_video оказались ненадёжны в этой версии TDLib —
                    // у видео-стикеров (stickerFormatWebm) оба флага были false.
                    // Настоящий, авторитетный признак формата — sticker.format.@type.
                    string stickerFormat = sticker?["format"]?["@type"]?.ToString() ?? "";
                    bool isWebpFormat = stickerFormat == "stickerFormatWebp";
                    bool isTgsFormat = stickerFormat == "stickerFormatTgs";
                    bool isWebmFormat = stickerFormat == "stickerFormatWebm";
                    item.IsSticker = true;
                    item.Text = "";
                    _messagesDict[msgId] = item;

                    var stickerFile = sticker?["sticker"] as JObject;
                    string stickerPath = stickerFile?["local"]?["path"]?.ToString() ?? "";

                    if (isWebpFormat) {
                        // Статичный WebP стикер — декодируем через libwebp
                        if (stickerFile != null) {
                            long sfid = (long)stickerFile["id"];
                            _fileToMsgId[sfid] = msgId;
                            string remoteUid = stickerFile["remote"]?["unique_id"]?.ToString();
                            if (!string.IsNullOrEmpty(remoteUid))
                                _remoteUniqueIdToMsgId[remoteUid] = msgId;
                            if (!string.IsNullOrEmpty(stickerPath))
                                { var t = UpdateMessagePhoto(msgId, stickerPath); }
                            else
                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + sfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    } else {
                        // Анимированный (.tgs) или видео (.webm) стикер — берём thumbnail
                        var thumb = sticker?["thumbnail"];
                        var thumbFile = thumb?["file"] as JObject;
                        bool thumbIsMpeg4 = thumb?["format"]?["@type"]?.ToString() == "thumbnailFormatMpeg4";
                        if (thumbFile != null) {
                            long tfid = (long)thumbFile["id"];
                            _fileToMsgId[tfid] = msgId;
                            string remoteUid = thumbFile["remote"]?["unique_id"]?.ToString();
                            if (!string.IsNullOrEmpty(remoteUid))
                                _remoteUniqueIdToMsgId[remoteUid] = msgId;
                            string tPath = thumbFile["local"]?["path"]?.ToString();
                            if (thumbIsMpeg4) {
                                // Миниатюра — короткий mp4-клип (не картинка). VP9/WebM
                                // самого стикера софтверно не декодируем — целиком
                                // отдельная большая задача, но H.264/MP4 этой
                                // миниатюры штатно тянет MediaElement.
                                _stickerVideoFileIds.Add(tfid);
                                if (!string.IsNullOrEmpty(tPath)) {
                                    item.IsStickerVideo = true;
                                    item.StickerVideoSource = new Uri(tPath);
                                } else {
                                    TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                                }
                            } else if (!string.IsNullOrEmpty(tPath)) {
                                var t = UpdateMessagePhoto(msgId, tPath);
                            } else {
                                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":10,\"synchronous\":false}");
                            }
                        } else if (stickerFile != null) {
                            // Thumbnail нет — пробуем скачать сам файл и смотрим что придёт
                            long sfid = (long)stickerFile["id"];
                        }
                    }
                } else if (type == "messagePoll") {
                    var poll = content["poll"];
                    if (poll != null) {
                        item.IsPoll = true;
                        item.Text = "";
                        item.PollQuestion = poll["question"]?["text"]?.ToString() ?? poll["question"]?.ToString() ?? "";
                        // Тип опроса
                        bool isAnonymous = poll["is_anonymous"]?.ToObject<bool>() ?? true;
                        bool isQuiz = poll["type"]?["@type"]?.ToString() == "pollTypeQuiz";
                        item.PollType = isQuiz ? "🎯 " + Loc.T("poll_quiz") : (isAnonymous ? "📊 " + Loc.T("poll_anonymous") : "📊 " + Loc.T("media_poll"));
                        // Варианты ответа
                        int totalVotes = poll["total_voter_count"]?.ToObject<int>() ?? 0;
                        var options = poll["options"] as JArray;
                        item.PollOptions.Clear();
                        if (options != null) {
                            for (int oi = 0; oi < options.Count; oi++) {
                                var opt = options[oi];
                                int votes = opt["voter_count"]?.ToObject<int>() ?? 0;
                                int pct = totalVotes > 0 ? (int)Math.Round(votes * 100.0 / totalVotes) : 0;
                                item.PollOptions.Add(new PollOptionItem {
                                    OptionId  = oi,
                                    MsgId     = msgId,
                                    TextColor = item.TextColor,
                                    Text      = opt["text"]?["text"]?.ToString() ?? opt["text"]?.ToString() ?? "",
                                    VoteCount = votes,
                                    Percent  = pct,
                                    IsChosen = opt["is_chosen"]?.ToObject<bool>() ?? false
                                });
                            }
                        }
                    }
                } else if (type == "messageDocument") {
                    var doc = content["document"];
                    var docFile = doc?["document"] as JObject;
                    string docName = doc?["file_name"]?.ToString() ?? Loc.T("media_file");
                    long docSize = docFile?["size"]?.ToObject<long>() ?? 0;
                    item.IsDocument = true;
                    item.DocumentName = docName;
                    item.DocumentSize = FormatFileSize(docSize);
                    if (docFile != null) {
                        long dfid = (long)docFile["id"];
                        _fileToMsgId[dfid] = msgId;
                        _messagesDict[msgId] = item;
                        bool isUploaded = docFile["remote"]?["is_uploading_completed"]?.ToObject<bool>() ?? false;
                        string dPath = docFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(dPath) && isUploaded) {
                            item.FilePath = dPath;
                            item.IsDownloaded = true;
                            item.DownloadStatus = Loc.T("status_open");
                        } else if (outgoing && !isUploaded) {
                            _uploadFileToMsgId[dfid] = msgId;
                            // Берём актуальный прогресс если updateFile уже пришёл
                            long alreadyUploaded = docFile["remote"]?["uploaded_size"]?.ToObject<long>() ?? 0;
                            long docTotal = docFile["expected_size"]?.ToObject<long>() ?? docFile["size"]?.ToObject<long>() ?? 0;
                            if (docTotal > 0 && alreadyUploaded > 0)
                                item.DownloadStatus = "⬆ " + (int)(alreadyUploaded * 100 / docTotal) + "%";
                            else
                                item.DownloadStatus = "⬆ 0%";
                        }
                    }
                } else if (type == "messageVoiceNote") {
                    var voiceNote = content["voice_note"];
                    var voiceFile = voiceNote?["voice"] as JObject;
                    int dur = voiceNote?["duration"]?.ToObject<int>() ?? 0;
                    item.IsAudio = true;
                    item.AudioTitle = "🎤 " + Loc.T("media_voice");
                    item.AudioDuration = dur > 0 ? FormatCallDuration(dur) : "";
                    item.AudioPlayStatus = "▶";
                    if (voiceFile != null) {
                        long vfid = (long)voiceFile["id"];
                        _fileToMsgId[vfid] = msgId;
                        _messagesDict[msgId] = item;
                        string vPath = voiceFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vPath)) {
                            item.FilePath = vPath;
                            item.DownloadStatus = "ready";
                        } else {
                            item.AudioPlayStatus = "⏳";
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + vfid + ",\"priority\":10,\"synchronous\":false}");
                        }
                    }
                } else if (type == "messageVideoNote") {
                    var videoNote = content["video_note"];
                    var videoFile = videoNote?["video"] as JObject;
                    int vnDur = videoNote?["duration"]?.ToObject<int>() ?? 0;
                    item.IsVideo = true;
                    item.Text = "⏺ " + (vnDur > 0 ? FormatCallDuration(vnDur) : Loc.T("media_videoMessage"));
                    if (videoFile != null) {
                        long vnFid = (long)videoFile["id"];
                        _fileToMsgId[vnFid] = msgId;
                        _videoFileIds[vnFid] = msgId;
                        _messagesDict[msgId] = item;
                        string vnPath = videoFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vnPath))
                            item.FilePath = vnPath;
                        else
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + vnFid + ",\"priority\":10,\"synchronous\":false}");
                    }
                    // Превью (миниатюра) — переиспользуем уже отлаженный путь
                    // декодирования (DecodePixelWidth для памяти, привязка по
                    // ссылке на объект, а не по id), тот же, что и у фото/стикеров.
                    // Раньше миниатюра ещё и не докачивалась вовсе, если её не
                    // было в кэше TDLib заранее — превью просто никогда не
                    // появлялось.
                    var vnThumb = videoNote?["thumbnail"]?["file"] as JObject;
                    if (vnThumb != null) {
                        long vnTfid = (long)vnThumb["id"];
                        _fileToMsgId[vnTfid] = msgId;
                        _inlinePhotoFileId[msgId] = vnTfid;
                        string vnTPath = vnThumb["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(vnTPath))
                            { var tvn = UpdateMessagePhoto(msgId, vnTPath); }
                        else
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + vnTfid + ",\"priority\":10,\"synchronous\":false}");
                    }
                } else if (type == "messageAudio") {
                    var audio = content["audio"];
                    var audioFile = audio?["audio"] as JObject;
                    string title = audio?["title"]?.ToString() ?? "";
                    string performer = audio?["performer"]?.ToString() ?? "";
                    int dur = audio?["duration"]?.ToObject<int>() ?? 0;
                    item.IsAudio = true;
                    item.AudioTitle = !string.IsNullOrEmpty(performer) ? performer + " — " + title
                                    : !string.IsNullOrEmpty(title) ? title : Loc.T("media_voiceMessage");
                    item.AudioDuration = dur > 0 ? FormatCallDuration(dur) : "";
                    item.AudioPlayStatus = "▶";
                    if (audioFile != null) {
                        long afid = (long)audioFile["id"];
                        _fileToMsgId[afid] = msgId;
                        _audioFileIds[afid] = msgId;
                        _messagesDict[msgId] = item;
                        string aPath = audioFile["local"]?["path"]?.ToString();
                        if (!string.IsNullOrEmpty(aPath)) {
                            item.FilePath = aPath;
                            item.DownloadStatus = "ready";
                        }
                        // Не качаем автоматически при открытии чата — только по клику
                        // на плеер (см. AudioButton_Click), как и с обычным видео.
                    }
                }
                if (string.IsNullOrEmpty(item.Text) && type != "messagePhoto" && type != "messageVideo" && type != "messageAnimation" && type != "messageDocument" && type != "messageAudio" && type != "messageVoiceNote" && type != "messageVideoNote" && type != "messageSticker" && type != "messagePoll") {
                    if (type == "messageCall") {
                        var callContent = content;
                        bool isVideo = callContent["is_video"]?.ToObject<bool>() ?? false;
                        string callEmoji = isVideo ? "📹" : "📞";
                        bool isOutgoing = (bool)msg["is_outgoing"];
                        string direction = isOutgoing ? Loc.T("callui_outgoing") : Loc.T("callui_incoming");
                        int duration = callContent["duration"]?.ToObject<int>() ?? 0;
                        string discardReason = callContent["discard_reason"]?["@type"]?.ToString() ?? "";
                        string durationStr = duration > 0 ? " · " + FormatCallDuration(duration) : "";
                        if (discardReason == "callDiscardReasonMissed")
                            item.Text = callEmoji + " " + Loc.T("callui_missed");
                        else if (discardReason == "callDiscardReasonDeclined")
                            item.Text = callEmoji + " " + Loc.T("callui_declined");
                        else
                            item.Text = callEmoji + " " + direction + " " + Loc.T("media_call") + durationStr;
                    } else if (type == "messageAudio") {
                        string title = content["audio"]?["title"]?.ToString() ?? "";
                        string performer = content["audio"]?["performer"]?.ToString() ?? "";
                        int dur = content["audio"]?["duration"]?.ToObject<int>() ?? 0;
                        string durStr = dur > 0 ? " · " + FormatCallDuration(dur) : "";
                        string label = !string.IsNullOrEmpty(performer) ? performer + " — " + title : title;
                        item.Text = "🎵 " + (string.IsNullOrEmpty(label) ? Loc.T("media_audio") : label) + durStr;
                    } else if (type == "messagePinMessage") {
                        long pinnedMsgId = content["message_id"]?.ToObject<long>() ?? 0;
                        // Получаем имя отправителя
                        string senderName = "";
                        var pinSenderId = msg["sender_id"];
                        if (pinSenderId?["@type"]?.ToString() == "messageSenderUser") {
                            long uid = pinSenderId["user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(uid)) {
                                var u = _usersDict[uid];
                                senderName = u["first_name"]?.ToString() ?? "";
                            }
                        }
                        item.Text = "📌 " + (string.IsNullOrEmpty(senderName) ? Loc.T("label_unknownUser") : senderName) + " " + Loc.T("svc_pinnedBySuffix");
                        // Запрашиваем текст закреплённого чтобы показать его
                        if (pinnedMsgId != 0) {
                            _pinnedTextRequests[pinnedMsgId] = msgId;
                            TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + (long)msg["chat_id"] + ",\"message_id\":" + pinnedMsgId + "}");
                        }
                    } else if (type == "messageChatAddMembers") {
                        // Кто добавил
                        string adderName = "";
                        var adderId = msg["sender_id"];
                        if (adderId?["@type"]?.ToString() == "messageSenderUser") {
                            long adderUid = adderId["user_id"]?.ToObject<long>() ?? 0;
                            if (_usersDict.ContainsKey(adderUid))
                                adderName = _usersDict[adderUid]["first_name"]?.ToString() ?? "";
                        }
                        if (string.IsNullOrEmpty(adderName)) adderName = Loc.T("label_unknownUser");

                        // Кого добавили — может быть несколько за раз
                        var addedIds = content["member_user_ids"] as JArray;
                        var addedNames = new List<string>();
                        if (addedIds != null) {
                            foreach (var addedIdToken in addedIds) {
                                long addedUid = addedIdToken.ToObject<long>();
                                string nm = _usersDict.ContainsKey(addedUid) ? _usersDict[addedUid]["first_name"]?.ToString() ?? "" : "";
                                addedNames.Add(string.IsNullOrEmpty(nm) ? Loc.T("label_unknownUser") : nm);
                            }
                        }
                        string namesJoined = addedNames.Count > 0 ? string.Join(", ", addedNames) : Loc.T("label_unknownUser");
                        item.Text = "➕ " + adderName + " " + Loc.T("svc_addedSuffix") + " " + namesJoined;
                    } else if (type == "messageRichMessage") {
                        string richText = ExtractRichMessageText(content);
                        item.Text = !string.IsNullOrEmpty(richText) ? richText : Loc.T("media_richMessage");
                    } else {
                        item.Text = "[" + type.Replace("message", "") + "]";
                    }
                }
                // Всегда регистрируем в словаре — нужно для редактирования и обновлений
                _messagesDict[msgId] = item;
                return item;
            } catch (Exception ex) { Log("ParseMessage ERR: " + ex.Message); return null; }
        }

        private async Task UpdateAvatarSearchResult(SearchResultItem item, string path) {
            try {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var bmp = new BitmapImage();
                bmp.DecodePixelWidth = 100;
                using (var stream = await file.OpenReadAsync())
                    await bmp.SetSourceAsync(stream);
                item.Photo = bmp;
            } catch { }
        }

        /// <summary>
        /// Аватарка отправителя в группах — грузим один раз на пользователя и
        /// переиспользуем везде, где он писал (в отличие от chat/contact
        /// аватарок, кэш тут не завязан на текущий чат и не чистится при
        /// его смене — id пользователя глобален, коллизий с другими чатами нет).
        /// </summary>
        private void EnsureSenderAvatar(long userId) {
            if (userId == 0 || _senderAvatarCache.ContainsKey(userId) || _senderAvatarRequested.Contains(userId)) return;
            if (!_usersDict.ContainsKey(userId)) return;
            var ph = _usersDict[userId]["profile_photo"]?["small"] as JObject;
            if (ph == null) return;
            long pfid = ph["id"]?.ToObject<long>() ?? 0;
            string pPath = ph["local"]?["path"]?.ToString();
            _senderAvatarRequested.Add(userId);
            if (!string.IsNullOrEmpty(pPath)) {
                var t = UpdateSenderAvatar(userId, pPath);
            } else if (pfid > 0) {
                _fileToSenderUserId[pfid] = userId;
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + pfid + ",\"priority\":1,\"synchronous\":false}");
            }
        }

        private async Task UpdateSenderAvatar(long userId, string path) {
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                bitmap.DecodePixelWidth = 80;
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                _senderAvatarCache[userId] = bitmap;
                // Проставляем всем уже отрисованным сообщениям от этого пользователя
                foreach (var m in _messageItems)
                    if (m.SenderUserId == userId) m.SenderPhoto = bitmap;
            } catch (Exception ex) { Log("UpdateSenderAvatar ERR user=" + userId + " | " + ex.Message); }
        }

        private async Task UpdateAvatar(long chatId, string path) {
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var bitmap = new BitmapImage();
                bitmap.DecodePixelWidth = 160;
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (_chatsDict.ContainsKey(chatId)) {
                    _chatsDict[chatId].Photo = bitmap;
                }
                // Если этот чат открыт — обновляем аватарку в шапке
                if (chatId == _currentChatId) {
                    ChatHeaderAvatarBrush.ImageSource = bitmap;
                    ChatHeaderAvatarEllipse.Visibility = Windows.UI.Xaml.Visibility.Visible;
                    ChatHeaderAvatarInitials.Text = "";
                }
            } catch (Exception ex) { Log("UpdateAvatar ERR chat=" + chatId + " | " + ex.Message); }
        }

        private async Task UpdateMessagePhoto(long msgId, string path) {
            // Держим ссылку на сам объект, а не искать заново по msgId в конце —
            // за время асинхронного декодирования id сообщения может смениться
            // (updateMessageSendSucceeded у только что отправленного сообщения),
            // а объект остаётся тем же самым и на нём же завязан биндинг в UI.
            // Ремап словарей (fileId → msgId) эту гонку не лечит: сам вызов уже
            // "в полёте" со старым id к моменту завершения декодирования.
            if (!_messagesDict.TryGetValue(msgId, out var targetItem)) return;
            try {
                // .tgs это gzip+lottie — не можем отобразить, пропускаем
                if (path.EndsWith(".tgs", StringComparison.OrdinalIgnoreCase)) {
                    return;
                }
                var file = await StorageFile.GetFileFromPathAsync(path);
                Windows.UI.Xaml.Media.ImageSource bitmap;

                if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) {
                    // WebP — декодируем через libwebp
                    byte[] data;
                    using (var stream = await file.OpenReadAsync())
                    using (var reader = new Windows.Storage.Streams.DataReader(stream)) {
                        await reader.LoadAsync((uint)stream.Size);
                        data = new byte[stream.Size];
                        reader.ReadBytes(data);
                    }
                    bitmap = await WebPDecoder.DecodeAsync(data);
                } else {
                    // Обычное изображение. DecodePixelWidth — чтобы декодер сразу
                    // уменьшал картинку при чтении, а не держал в памяти полный
                    // размер файла ради превью шириной ~250px на экране.
                    var bmp = new BitmapImage();
                    bmp.DecodePixelWidth = 500;
                    using (var stream = await file.OpenReadAsync())
                        await bmp.SetSourceAsync(stream);
                    bitmap = bmp;
                }

                if (bitmap != null) {
                    targetItem.AttachedPhoto = bitmap;
                }
            } catch (Exception ex) {
                Log("UpdateMsgPhoto ERR msg=" + msgId + " | " + ex.Message);
            }
        }

        

        private void ChatListView_ItemClick(object sender, ItemClickEventArgs e) {
            var chat = (ChatItem)e.ClickedItem;
            if (chat.Id == _currentChatId && _threadMessageId == 0) return;
            _threadMessageId = 0;
            _threadChatId = 0;
            // Очищаем поиск
            if (!string.IsNullOrEmpty(_searchQuery)) {
                SearchBox.Text = "";
                _searchQuery = "";
                SearchClearButton.Visibility = Visibility.Collapsed;
                SearchResultsView.Visibility = Visibility.Collapsed;
                ChatListView.Visibility = Visibility.Visible;
                        if (SearchPanel != null) SearchPanel.Visibility = Visibility.Visible;
            }
            if (_chatsDict.ContainsKey(chat.Id))
                OpenChat(_chatsDict[chat.Id], 0);
        }

        // Открыть чат по ID (используется при возврате из треда)
        private void OpenChatById(long chatId) {
            if (!_chatsDict.ContainsKey(chatId)) return;
            var chat = _chatsDict[chatId];
            // Эмулируем клик по чату
            var fakeItem = new ChatItem { Id = chatId, Title = chat.Title,
                Photo = chat.Photo, IsChannel = chat.IsChannel, OutboxReadId = chat.OutboxReadId };
            OpenChat(fakeItem, 0);
        }

        // Открыть тред комментариев поста
        private void PollOption_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Windows.UI.Xaml.Controls.Button;
            var opt = btn?.Tag as PollOptionItem;
            if (opt == null) return;
            string req = "{\"@type\":\"setPollAnswer\",\"chat_id\":" + _currentChatId +
                         ",\"message_id\":" + opt.MsgId +
                         ",\"option_ids\":[" + opt.OptionId + "]}";
            TdJson.SendUtf8(_client, req);
            // После небольшой задержки перезагружаем сообщение
            long msgId = opt.MsgId;
            var timer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (s2, e2) => {
                timer.Stop();
                TdJson.SendUtf8(_client, "{\"@type\":\"getMessage\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + msgId + "}");
            };
            timer.Start();
        }

        private void CommentsButton_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Windows.UI.Xaml.Controls.Button;
            if (btn == null) return;
            long msgId = (long)btn.Tag;
            _threadChatId = _currentChatId;
            _threadMessageId = msgId;
            TdJson.SendUtf8(_client, "{\"@type\":\"getMessageThread\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + msgId + "}");
        }

        // Открыть чат с опциональным thread_id
        private void OpenChat(ChatItem chat, long threadId) {
            if (_currentChatId != 0)
                TdJson.SendUtf8(_client, "{\"@type\":\"closeChat\",\"chat_id\":" + _currentChatId + "}");
            _currentChatId = chat.Id;
            RemoveToastsForChat(chat.Id);   // чат открыт — уведомления по нему больше не нужны
            ClearTilePreviewsForChat(chat.Id);
            // Группа если тип chatTypeBasicGroup или chatTypeSupergroup не-канал
            _currentChatIsGroup = false;
            _currentChatIsChannel = false;
            if (_rawChatsDict.ContainsKey(chat.Id)) {
                var rawC = _rawChatsDict[chat.Id] as Newtonsoft.Json.Linq.JObject;
                string ctype = rawC?["type"]?["@type"]?.ToString() ?? "";
                bool isSupergroup = ctype == "chatTypeSupergroup";
                bool isChannel = rawC?["type"]?["is_channel"]?.ToObject<bool>() ?? false;
                _currentChatIsGroup = ctype == "chatTypeBasicGroup" || (isSupergroup && !isChannel);
                _currentChatIsChannel = isSupergroup && isChannel;
            }
            _pendingHistoryChatId = chat.Id;
            _historyRetryCount = 0;
            _loadingOlderHistory = false;

            _hasMoreHistory = true;



            _trimming = false;
            _autoScrolling = false;
            _scrollTimer?.Stop();
            _restoreTimer?.Stop();
            if (OlderLoadingIndicator != null) {
                OlderLoadingIndicator.Visibility = Visibility.Collapsed;
                OlderProgressRing.IsActive = false;
            }
            if (ScrollToBottomButton != null)
                ScrollToBottomButton.Visibility = Visibility.Collapsed;
            _currentChatOutboxReadId = chat.OutboxReadId;
            // Сохраняем последнее прочитанное входящее — для разделителя "Новые сообщения"
            _lastReadInboxMsgId = 0;
            if (_chatsDict.ContainsKey(chat.Id)) {
                var rawChat = _rawChatsDict.ContainsKey(chat.Id) ? _rawChatsDict[chat.Id] : null;
                if (rawChat != null)
                    _lastReadInboxMsgId = rawChat["last_read_inbox_message_id"]?.ToObject<long>() ?? 0;
            }
            _messageItems.Clear();
            _messagesDict.Clear();
            _fileToMsgId.Clear();
            _inlinePhotoFileId.Clear();
            _stickerVideoFileIds.Clear();
            _videoFileIds.Clear();
            _audioFileIds.Clear();
            _replyRequests.Clear();
            _editRefreshPendingIds.Clear();
            ChatSearchBar.Visibility = Visibility.Collapsed;
            ChatSearchResultsView.Visibility = Visibility.Collapsed;
            ChatHeader.Visibility = Visibility.Visible;
            _chatSearchQuery = "";
            _chatSearchResultIds.Clear();
            _chatSearchResultItems.Clear();
            _chatSearchResultIndex = -1;
            _chatSearchAwaitingResults = false;
            _remoteUniqueIdToMsgId.Clear();
            _editingMessageId = 0;
            _replyToMessageId = 0;
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
            ReplyPreviewText.Text = "";
            _fullPhotoMsgId = 0;
            PhotoOverlay.Visibility = Visibility.Collapsed;
            PhotoOverlayImage.Source = null;
            MessageInput.Text = "";
            SendButton.Content = "➤";
            StartPanel.Visibility = Visibility.Collapsed;
            MessagesPanel.Visibility = Visibility.Visible;
            // Заголовок — если тред, показываем "Комментарии"
            CurrentChatTitle.Text = threadId != 0 ? Loc.T("label_comments") : chat.Title;
            UpdateChatCallButton(chat.Id, threadId);
            if (threadId != 0) {
                CurrentChatStatus.Text = "← " + chat.Title;
                CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
            } else if (_usersDict.ContainsKey(chat.Id)) {
                UpdateChatStatus(_usersDict[chat.Id]["status"]);
            } else if (chat.IsChannel) {
                CurrentChatStatus.Text = Loc.T("label_channel");
                CurrentChatStatus.Foreground = CB(_isLightTheme ? "#000000" : "#CCE8FF");
            } else {
                CurrentChatStatus.Text = "";
                // Запрашиваем пользователя — статус появится когда придёт updateUser
                TdJson.SendUtf8(_client, "{\"@type\":\"getUser\",\"user_id\":" + chat.Id + "}");
            }
            InputBorder.Visibility = (chat.IsChannel && threadId == 0) ? Visibility.Collapsed : Visibility.Visible;
            // Проверяем бот ли это
            _currentChatIsBot = false;
            StartBotButton.Visibility = Visibility.Collapsed;
            if (_rawChatsDict.ContainsKey(chat.Id)) {
                var rawC = _rawChatsDict[chat.Id] as Newtonsoft.Json.Linq.JObject;
                long botUserId = rawC?["type"]?["user_id"]?.ToObject<long>() ?? 0;
                if (botUserId != 0 && _usersDict.ContainsKey(botUserId)) {
                    string utype = _usersDict[botUserId]["type"]?["@type"]?.ToString() ?? "";
                    _currentChatIsBot = utype == "userTypeBot";
                }
            }
            // Аватарка — "Избранное" всегда звезда на голубом кружке, даже если
            // у chat.Photo реально лежит собственное фото профиля пользователя.
            if (chat.IsSavedMessages) {
                ChatHeaderAvatarBrush.ImageSource = null;
                ChatHeaderAvatarEllipse.Visibility = Visibility.Collapsed;
                ChatHeaderAvatarBorder.Fill = CB("#2AABEE");
                ChatHeaderAvatarInitials.Text = "★";
            } else {
                if (chat.Photo != null) ChatHeaderAvatarBrush.ImageSource = chat.Photo;
                else ChatHeaderAvatarBrush.ImageSource = null;
                ChatHeaderAvatarEllipse.Visibility = chat.Photo != null ? Visibility.Visible : Visibility.Collapsed;
                ChatHeaderAvatarBorder.Fill = CB(AvatarPlaceholder.GetColor(chat.Id));
                ChatHeaderAvatarInitials.Text = chat.Photo != null ? "" : AvatarPlaceholder.GetInitials(chat.Title);
            }
            // Статус для групп/каналов — запрашиваем число участников
            if (_currentChatIsGroup || chat.IsChannel) {
                CurrentChatStatus.Text = Loc.T("status_loading");
                if (_rawChatsDict.ContainsKey(chat.Id)) {
                    var rawC2 = _rawChatsDict[chat.Id] as Newtonsoft.Json.Linq.JObject;
                    long sgId2 = rawC2?["type"]?["supergroup_id"]?.ToObject<long>() ?? 0;
                    long bgId2 = rawC2?["type"]?["basic_group_id"]?.ToObject<long>() ?? 0;
                    if (sgId2 != 0)
                        TdJson.SendUtf8(_client, "{\"@type\":\"getSupergroup\",\"supergroup_id\":" + sgId2 + "}");
                    else if (bgId2 != 0)
                        TdJson.SendUtf8(_client, "{\"@type\":\"getBasicGroup\",\"basic_group_id\":" + bgId2 + "}");
                }
            }
            TdJson.SendUtf8(_client, "{\"@type\":\"getChatPinnedMessage\",\"chat_id\":" + chat.Id + "}");
            TdJson.SendUtf8(_client, "{\"@type\":\"getChatPinnedMessage\",\"chat_id\":" + chat.Id + "}");
            _isLoadingHistory = true;
            LoadingIndicator.Visibility = Visibility.Visible;
            MessagesListView.Visibility = Visibility.Collapsed;
            TdJson.SendUtf8(_client, "{\"@type\":\"openChat\",\"chat_id\":" + _currentChatId + "}");
            string histReq = threadId != 0
                ? "{\"@type\":\"getMessageThreadHistory\",\"chat_id\":" + _currentChatId + ",\"message_id\":" + threadId + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}"
                : "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId + ",\"from_message_id\":0,\"offset\":0,\"limit\":50}";
            TdJson.SendUtf8(_client, histReq);
        }

        private void ForwardMessage_Click(object sender, RoutedEventArgs e) {
            if (_pendingContextMsg == null) return;
            _forwardMessageIds = new List<long> { _pendingContextMsg.Id };
            _forwardFromChatId = _currentChatId;
            ForwardChatList.ItemsSource = _chatListItems.Concat(_archiveChatItems).ToList();
            ForwardChatOverlay.Visibility = Visibility.Visible;
        }

        private void ForwardOverlay_Close(object sender, RoutedEventArgs e) {
            ForwardOverlay.Visibility = Visibility.Collapsed;
        }

        private void ForwardChatListOld_ItemClick(object sender, ItemClickEventArgs e) {
            var targetChat = e.ClickedItem as ChatItem;
            if (targetChat == null || _pendingContextMsg == null) return;
            ForwardOverlay.Visibility = Visibility.Collapsed;

            long fromChatId = _currentChatId;
            long msgId = _pendingContextMsg.Id;
            _pendingContextMsg = null;

            // forwardMessages с send_copy=false — сохраняет оригинального отправителя в заголовке
            var req = new JObject {
                ["@type"] = "forwardMessages",
                ["chat_id"] = targetChat.Id,
                ["from_chat_id"] = fromChatId,
                ["message_ids"] = new JArray { msgId },
                ["send_copy"] = false,
                ["remove_caption"] = false
            };
            TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void React_Click(object sender, RoutedEventArgs e) {
            var item = sender as MenuFlyoutItem;
            if (item == null || _selectedMessageForCopy == null) return;
            string emoji = item.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(emoji)) return;
            bool alreadyReacted = _selectedMessageForCopy.Reactions != null &&
                                  _selectedMessageForCopy.Reactions.Contains(emoji);
            string req = "{\"@type\":\"" + (alreadyReacted ? "removeMessageReaction" : "addMessageReaction") + "\"" +
                ",\"chat_id\":" + _currentChatId +
                ",\"message_id\":" + _selectedMessageForCopy.Id +
                ",\"reaction_type\":{\"@type\":\"reactionTypeEmoji\",\"emoji\":\"" + emoji + "\"}" +
                (alreadyReacted ? "" : ",\"is_big\":false") + "}";
            TdJson.SendUtf8(_client, req);
        }

        private void ReplyMessage_Click(object sender, RoutedEventArgs e) {
            var msg = _pendingContextMsg;
            if (msg == null) return;
            _replyToMessageId = msg.Id;
            // Текст превью — первые 80 символов
            string preview = string.IsNullOrEmpty(msg.Text) ? "(медиа)" : msg.Text;
            if (preview.Length > 80) preview = preview.Substring(0, 80) + "…";
            ReplyPreviewText.Text = preview;
            ReplyPreviewPanel.Visibility = Visibility.Visible;
            MessageInput.Focus(FocusState.Programmatic);
        }

        private void CancelReply_Click(object sender, RoutedEventArgs e) {
            _replyToMessageId = 0;
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
            ReplyPreviewText.Text = "";
        }

        private void SendMessage_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(MessageInput.Text)) return;
            // TextBox с AcceptsReturn="True" хранит перенос строки как
            // одиночный \r (0x0D), а не \n. Если отправить это как есть,
            // TDLib/сервер не распознаёт голый \r как разделитель строк и
            // просто вырезает его при санитизации текста — собеседник видит
            // все строки склеенными без единого пробела. Нормализуем к \n
            // ДО отправки, а не только при отображении уже полученного текста.
            string text = MessageInput.Text.Replace("\r\n", "\n").Replace("\r", "\n");
            MessageInput.Text = "";

            // Режим редактирования
            if (_editingMessageId != 0) {
                long editId = _editingMessageId;
                _editingMessageId = 0;
                SendButton.Content = "➤";
                JObject req = new JObject {
                    ["@type"] = "editMessageText",
                    ["chat_id"] = _currentChatId,
                    ["message_id"] = editId,
                    ["input_message_content"] = new JObject {
                        ["@type"] = "inputMessageText",
                        ["text"] = new JObject { ["@type"] = "formattedText", ["text"] = text }
                    }
                };
                TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
                // Обновляем UI сразу — не ждём updateMessageEdited (он не содержит нового текста)
                if (_messagesDict.ContainsKey(editId)) {
                    _messagesDict[editId].Text = text;
                    _messagesDict[editId].IsEdited = true;
                }
                // Тап по кнопке уводит фокус с TextBox на саму кнопку, а клавиатура
                // в UWP скрывается/показывается по типу элемента в фокусе — без
                // этого клавиатура пряталась бы после каждой отправки/редактирования.
                MessageInput.Focus(FocusState.Programmatic);
                return;
            }

            JObject sendReq = new JObject {
                ["@type"] = "sendMessage",
                ["chat_id"] = _currentChatId,
                ["input_message_content"] = new JObject {
                    ["@type"] = "inputMessageText",
                    ["text"] = new JObject { ["@type"] = "formattedText", ["text"] = text }
                }
            };
            // Если открыт тред комментариев — привязываем сообщение к треду.
            // В TDLib 1.8.66 у сообщения больше нет плоского message_thread_id —
            // его заменил topic_id типа MessageTopic (видно в самом ответе
            // messageThreadInfo). Старый параметр TDLib молча игнорировал,
            // поэтому комментарий уходил в группу обсуждений обычным сообщением,
            // без привязки к посту, и никакой ошибки при этом не возникало.
            // Старое поле оставлено рядом: неизвестные параметры TDLib
            // пропускает без ошибки, так что на более старой сборке сработает оно.
            if (_threadMessageId != 0) {
                sendReq["topic_id"] = new JObject {
                    ["@type"] = "messageTopicThread",
                    ["message_thread_id"] = _threadMessageId
                };
                sendReq["message_thread_id"] = _threadMessageId;
            }
            if (_replyToMessageId != 0) {
                sendReq["reply_to"] = new JObject {
                    ["@type"] = "inputMessageReplyToMessage",
                    ["message_id"] = _replyToMessageId
                };
                _replyToMessageId = 0;
                ReplyPreviewPanel.Visibility = Visibility.Collapsed;
                ReplyPreviewText.Text = "";
            }
            TdJson.SendUtf8(_client, sendReq.ToString(Newtonsoft.Json.Formatting.None));
            // Аналогично — иначе клавиатура прячется после каждой отправки,
            // потому что фокус после тапа по кнопке уходит с текстового поля.
            MessageInput.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// JSON-фрагмент привязки сообщения к треду комментариев (или пустая
        /// строка, если тред не открыт). В TDLib 1.8.66 привязка задаётся через
        /// topic_id типа MessageTopic — прежний плоский message_thread_id
        /// библиотека молча игнорирует, из-за чего комментарий уходил в группу
        /// обсуждений обычным сообщением. Старое поле оставлено рядом: лишние
        /// параметры TDLib пропускает без ошибки, так что на более старой
        /// сборке сработает оно.
        /// </summary>
        private string ThreadJsonPart() {
            if (_threadMessageId == 0) return "";
            return ",\"topic_id\":{\"@type\":\"messageTopicThread\",\"message_thread_id\":" + _threadMessageId + "}" +
                   ",\"message_thread_id\":" + _threadMessageId;
        }

        private void SubscribeRichText(Windows.UI.Xaml.Controls.RichTextBlock rtb, MessageItem item) {
            BuildRichText(rtb, item);
            item.PropertyChanged += async (s, e2) => {
                if (e2.PropertyName == "Text")
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () => BuildRichText(rtb, rtb.DataContext as MessageItem ?? item));
            };
        }

        private void MsgRichText_DataContextChanged(Windows.UI.Xaml.FrameworkElement sender, Windows.UI.Xaml.DataContextChangedEventArgs args) {
            var rtb = sender as Windows.UI.Xaml.Controls.RichTextBlock;
            if (rtb == null) return;
            var item = rtb.DataContext as MessageItem;
            if (item == null) return;
            SubscribeRichText(rtb, item);
        }

        private void MsgRichText_Loaded(object sender, RoutedEventArgs e) {
            var rtb = sender as Windows.UI.Xaml.Controls.RichTextBlock;
            if (rtb == null) return;
            var item = rtb.DataContext as MessageItem;
            if (item == null) return;
            SubscribeRichText(rtb, item);
        }

        /// <summary>
        /// Run.Text внутри RichTextBlock/Paragraph игнорирует встроенные \n —
        /// перенос строки не отрисовывается сам по себе (это простой строчный
        /// инлайн, а не блочный элемент). Поэтому многострочные сообщения нужно
        /// резать по переносам и вставлять между кусками отдельный LineBreak.
        /// Заодно нормализуем \r\n и одиночный \r (так UWP TextBox с
        /// AcceptsReturn хранит перенос строки) к \n.
        /// </summary>
        private static void AddTextWithLineBreaks(Windows.UI.Xaml.Documents.InlineCollection inlines, string text) {
            if (string.IsNullOrEmpty(text)) return;
            var parts = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            for (int i = 0; i < parts.Length; i++) {
                if (parts[i].Length > 0)
                    inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = parts[i] });
                if (i < parts.Length - 1)
                    inlines.Add(new Windows.UI.Xaml.Documents.LineBreak());
            }
        }

        private void BuildRichText(Windows.UI.Xaml.Controls.RichTextBlock rtb, MessageItem item) {
            rtb.Blocks.Clear();
            var para = new Windows.UI.Xaml.Documents.Paragraph();
            string text = item.Text ?? "";
            Windows.UI.Color linkColor;
            if (_isLightTheme) {
                // Светлая тема: синий на зелёном и белом фоне
                linkColor = Windows.UI.Color.FromArgb(255, 33, 150, 243); // #2196F3
            } else {
                // Тёмная тема: исходящие — светло-жёлтый, входящие — голубой
                linkColor = item.IsOutgoing
                    ? Windows.UI.Color.FromArgb(255, 255, 229, 127)  // #FFE57F
                    : Windows.UI.Color.FromArgb(255, 100, 200, 255); // #64C8FF
            }

            if (item.Entities == null || item.Entities.Count == 0) {
                AddTextWithLineBreaks(para.Inlines, text);
            } else {
                int pos = 0;
                var sorted = item.Entities.OrderBy(x => x.Offset).ToList();
                foreach (var ent in sorted) {
                    int offset = ent.Offset, length = ent.Length;
                    string url = ent.Url;
                    if (offset > pos)
                        AddTextWithLineBreaks(para.Inlines, text.Substring(pos, offset - pos));
                    int safeLen = Math.Min(length, text.Length - offset);
                    if (safeLen > 0 && offset < text.Length) {
                        string linkText = text.Substring(offset, safeLen);
                        try {
                            var hl = new Windows.UI.Xaml.Documents.Hyperlink {
                                NavigateUri = new Uri(url.StartsWith("http") ? url : "https://" + url),
                                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(linkColor)
                            };
                            AddTextWithLineBreaks(hl.Inlines, linkText);
                            para.Inlines.Add(hl);
                        } catch {
                            AddTextWithLineBreaks(para.Inlines, linkText);
                        }
                    }
                    pos = offset + safeLen;
                }
                if (pos < text.Length)
                    AddTextWithLineBreaks(para.Inlines, text.Substring(pos));
            }
            rtb.Blocks.Add(para);
        }

        private void PhotoImage_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            e.Handled = true;
            var img = sender as Image;
            var item = img?.DataContext as MessageItem;
            if (item == null || item.IsVideo) return;

            // Показываем оверлей сразу с превью
            PhotoOverlay.Visibility = Visibility.Visible;
            PhotoOverlayImage.Source = item.AttachedPhoto;
            PhotoOverlayStatus.Text = Loc.T("status_loadingFullSize");
            _currentPhotoOverlayPath = null;
            _pendingPhotoSave = false;

            if (item.FullPhotoFileId == 0) { PhotoOverlayStatus.Text = ""; return; }

            // Запрашиваем полноразмерный файл
            _fullPhotoMsgId = item.Id;
            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + item.FullPhotoFileId + ",\"priority\":32,\"synchronous\":false}");
        }

        private void PhotoOverlay_Tapped(object sender, RoutedEventArgs e) {
            PhotoOverlay.Visibility = Visibility.Collapsed;
            PhotoOverlayImage.Source = null;
            _fullPhotoMsgId = 0;
            _currentPhotoOverlayPath = null;
            _pendingPhotoSave = false;
        }

        /// <summary>Кнопка "💾" в полноэкранном просмотре фото.</summary>
        private void PhotoOverlaySave_Click(object sender, RoutedEventArgs e) {
            if (!string.IsNullOrEmpty(_currentPhotoOverlayPath))
                SaveAndToast(_currentPhotoOverlayPath, Windows.Storage.Pickers.PickerLocationId.PicturesLibrary);
            else
                _pendingPhotoSave = true; // полный размер ещё не докачался — сохраним, как только будет готов
        }

        private async Task ShowFullPhoto(string path) {
            try {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using (var stream = await file.OpenReadAsync()) {
                    var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    PhotoOverlayImage.Source = bitmap;
                    PhotoOverlayStatus.Text = "";
                }
                _currentPhotoOverlayPath = path;
                if (_pendingPhotoSave) {
                    _pendingPhotoSave = false;
                    SaveAndToast(path, Windows.Storage.Pickers.PickerLocationId.PicturesLibrary);
                }
            } catch (Exception ex) { Log("FULLPHOTO ERR: " + ex.Message); }
        }

        private async void MessagesListView_ItemClick(object sender, ItemClickEventArgs e) {
            var item = e.ClickedItem as MessageItem;
            if (item == null || !item.IsVideo) return;
            if (string.IsNullOrEmpty(item.FilePath)) {
                foreach (var kv in _videoFileIds)
                    if (kv.Value == item.Id) {
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":32,\"synchronous\":false}");
                        break;
                    }
                return;
            }
            try {
                var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                await Windows.System.Launcher.LaunchFileAsync(file);
            } catch (Exception ex) { Log("VIDEO ERR: " + ex.Message); }
        }

        private async void DocumentButton_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e) {
            var btn = sender as Windows.UI.Xaml.Controls.Button;
            if (btn?.Tag == null) return;
            long msgId = (long)btn.Tag;
            if (!_messagesDict.ContainsKey(msgId)) return;
            var item = _messagesDict[msgId];
            if (item.IsDownloaded && !string.IsNullOrEmpty(item.FilePath)) {
                try {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.FilePath);
                    await Windows.System.Launcher.LaunchFileAsync(file);
                } catch (Exception ex) { Log("DOC open ERR: " + ex.Message); }
            } else {
                // Запускаем скачивание — ищем file_id по msgId
                foreach (var kv in _fileToMsgId) {
                    if (kv.Value == msgId) {
                        item.DownloadStatus = Loc.T("status_loadingEllipsis");
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":10,\"synchronous\":false}");
                        break;
                    }
                }
            }
        }

        private string BuildReactionsString(JArray reactions) {
            var parts = new System.Text.StringBuilder();
            foreach (var r in reactions) {
                string emoji = r["type"]?["emoji"]?.ToString() ?? "👍";
                int count = r["total_count"]?.ToObject<int>() ?? 0;
                if (count > 0) {
                    if (parts.Length > 0) parts.Append("  ");
                    parts.Append(emoji);
                    if (count > 1) parts.Append(" " + count);
                }
            }
            return parts.ToString();
        }

        private async void AttachFile_Click(object sender, RoutedEventArgs e) {
            if (_currentChatId == 0) return;
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary;
            picker.FileTypeFilter.Add("*");
            // Выбираем несколько файлов
            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;
            foreach (var file in files) {
                var copy = await file.CopyAsync(_filesFolder, file.Name, Windows.Storage.NameCollisionOption.ReplaceExisting);
                string path = copy.Path.Replace("\\", "/");
                string ext = file.FileType?.ToLower() ?? "";
                bool isPhoto = ext == ".jpg" || ext == ".jpeg" || ext == ".png" || ext == ".webp" || ext == ".bmp";
                string req;
                if (isPhoto) {
                    req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId + ThreadJsonPart() +
                        ",\"input_message_content\":{\"@type\":\"inputMessagePhoto\"" +
                        ",\"photo\":{\"@type\":\"inputPhoto\"" +
                        ",\"photo\":{\"@type\":\"inputFileLocal\",\"path\":\"" + path.Replace("\"","\\\"") + "\"}}" +
                        ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                } else {
                    req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId + ThreadJsonPart() +
                        ",\"input_message_content\":{\"@type\":\"inputMessageDocument\"" +
                        ",\"document\":{\"@type\":\"inputDocument\"" +
                        ",\"document\":{\"@type\":\"inputFileLocal\",\"path\":\"" + path.Replace("\"","\\\"") + "\"}" +
                        ",\"disable_content_type_detection\":false}" +
                        ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                }
                TdJson.SendUtf8(_client, req);
            }
        }

        private bool _pendingOpenChat = false; // ждём chat после searchPublicChat/createPrivateChat для открытия

        private void OpenMention_Click(object sender, RoutedEventArgs e) {
            var mentions = _selectedMessageForCopy?.Entities?.Where(en => en.Mention != null).ToList();
            if (mentions == null || mentions.Count == 0) return;
            string mention = mentions[0].Mention;
            _pendingOpenChat = true;
            if (mention.StartsWith("@id")) {
                long uid = 0;
                long.TryParse(mention.Substring(3), out uid);
                if (uid != 0)
                    TdJson.SendUtf8(_client, "{\"@type\":\"createPrivateChat\",\"user_id\":" + uid + ",\"force\":true}");
                else _pendingOpenChat = false;
            } else {
                string username = mention.TrimStart('@');
                TdJson.SendUtf8(_client, "{\"@type\":\"searchPublicChat\",\"username\":\"" + username + "\"}");
            }
        }

        /// <summary>Пункт "💾 Сохранить" в контекстном меню сообщения с видео.</summary>
        private void SaveVideoMessage_Click(object sender, RoutedEventArgs e) {
            var item = _selectedMessageForCopy;
            if (item == null) return;
            if (!string.IsNullOrEmpty(item.FilePath)) {
                SaveAndToast(item.FilePath, Windows.Storage.Pickers.PickerLocationId.VideosLibrary);
                return;
            }
            // Ещё не скачано — докачиваем и сохраняем сразу по завершении
            _pendingSaveMsgIds.Add(item.Id);
            foreach (var kv in _videoFileIds) {
                if (kv.Value == item.Id) {
                    TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":32,\"synchronous\":false}");
                    break;
                }
            }
        }

        private void PinMessage_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null || _currentChatId == 0) return;
            bool isPinned = _selectedMessageForCopy.Id == _pinnedMessageId && _pinnedMessageId != 0;
            if (isPinned) {
                // Открепляем
                TdJson.SendUtf8(_client, "{\"@type\":\"unpinChatMessage\",\"chat_id\":" + _currentChatId +
                    ",\"message_id\":" + _selectedMessageForCopy.Id + "}");
                _pinnedMessageId = 0;
                PinnedMessageBar.Visibility = Visibility.Collapsed;
                PinnedMessageText.Text = "";
            } else {
                // Закрепляем
                TdJson.SendUtf8(_client, "{\"@type\":\"pinChatMessage\",\"chat_id\":" + _currentChatId +
                    ",\"message_id\":" + _selectedMessageForCopy.Id +
                    ",\"disable_notification\":false,\"only_for_self\":false}");
                _pinnedMessageId = _selectedMessageForCopy.Id;
                // Обновляем текст полоски
                string pinText = !string.IsNullOrEmpty(_selectedMessageForCopy.Text)
                    ? _selectedMessageForCopy.Text
                    : Loc.T("media_message");
                PinnedMessageText.Text = pinText;
                PinnedMessageBar.Visibility = Visibility.Visible;
            }
        }

        private void PinnedMessage_Click(object sender, RoutedEventArgs e) {
            if (_pinnedMessageId <= 0) return;
            var pinned = _messageItems.FirstOrDefault(m => !m.IsSeparator && m.Id == _pinnedMessageId);
            if (pinned != null) {
                // Вычисляем позицию через индекс и среднюю высоту
                int idx = _messageItems.IndexOf(pinned);
                double sh = MessagesScrollViewer.ScrollableHeight;
                double itemH = sh / Math.Max(_messageItems.Count, 1);
                double target = Math.Max(0, idx * itemH - 60);
                MessagesScrollViewer.ChangeView(null, target, null, false);
            } else {
                // Сообщение не загружено — запрашиваем историю вокруг него
                _pendingScrollToMsgId = _pinnedMessageId;
                TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId +
                    ",\"from_message_id\":" + _pinnedMessageId + ",\"offset\":-10,\"limit\":20}");
            }
        }

        // ======= Поиск внутри текущей переписки =======

        private void ChatSearchButton_Click(object sender, RoutedEventArgs e) {
            if (_currentChatId == 0) return;
            if (ChatSearchResultsView.ItemsSource == null) ChatSearchResultsView.ItemsSource = _chatSearchResultItems;
            ChatHeader.Visibility = Visibility.Collapsed;
            ChatSearchBar.Visibility = Visibility.Visible;
            ChatSearchBox.Text = "";
            ChatSearchCounter.Text = "";
            _chatSearchResultIds.Clear();
            _chatSearchResultItems.Clear();
            _chatSearchResultIndex = -1;
            ChatSearchResultsView.Visibility = Visibility.Collapsed;
            ChatSearchBox.Focus(FocusState.Programmatic);
        }

        private void ChatSearchClose_Click(object sender, RoutedEventArgs e) {
            ChatSearchBar.Visibility = Visibility.Collapsed;
            ChatSearchResultsView.Visibility = Visibility.Collapsed;
            ChatHeader.Visibility = Visibility.Visible;
            ChatSearchBox.Text = "";
            _chatSearchQuery = "";
            _chatSearchResultIds.Clear();
            _chatSearchResultItems.Clear();
            _chatSearchResultIndex = -1;
            _chatSearchAwaitingResults = false;
        }

        private void ChatSearchBox_TextChanged(object sender, Windows.UI.Xaml.Controls.TextChangedEventArgs e) {
            _chatSearchQuery = ChatSearchBox.Text ?? "";
            if (_chatSearchDebounceTimer == null) {
                _chatSearchDebounceTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _chatSearchDebounceTimer.Tick += (ts, te) => {
                    _chatSearchDebounceTimer.Stop();
                    RunChatSearch();
                };
            }
            _chatSearchDebounceTimer.Stop();
            if (string.IsNullOrWhiteSpace(_chatSearchQuery)) {
                _chatSearchResultIds.Clear();
                _chatSearchResultItems.Clear();
                _chatSearchResultIndex = -1;
                ChatSearchCounter.Text = "";
                ChatSearchResultsView.Visibility = Visibility.Collapsed;
                return;
            }
            _chatSearchDebounceTimer.Start();
        }

        private void RunChatSearch() {
            if (_currentChatId == 0 || string.IsNullOrWhiteSpace(_chatSearchQuery)) return;
            _chatSearchAwaitingResults = true;
            string q = _chatSearchQuery.Replace("\\", "\\\\").Replace("\"", "\\\"");
            TdJson.SendUtf8(_client, "{\"@type\":\"searchChatMessages\",\"chat_id\":" + _currentChatId +
                ",\"query\":\"" + q + "\",\"from_message_id\":0,\"offset\":0,\"limit\":50}");
        }

        /// <summary>Ответ на searchChatMessages — в TDLib 1.8.66 приходит как "foundChatMessages"
        /// (см. case "foundChatMessages"), а не "messages" — обрабатывается тут вне зависимости
        /// от того, из какой именно ветки switch-а был вызван этот метод.</summary>
        private void HandleChatSearchResults(JToken update) {
            var found = update["messages"] as JArray;
            _chatSearchResultIds.Clear();
            _chatSearchResultItems.Clear();
            if (found != null) {
                foreach (var fm in found) {
                    long fmId = fm["id"]?.ToObject<long>() ?? 0;
                    if (fmId == 0) continue;
                    _chatSearchResultIds.Add(fmId);

                    string snippet = fm["content"]?["text"]?["text"]?.ToString()
                                   ?? fm["content"]?["caption"]?["text"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(snippet)) snippet = Loc.T("media_message");
                    int fmDate = fm["date"]?.ToObject<int>() ?? 0;
                    string fmDateStr = fmDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(fmDate).LocalDateTime.ToString("dd.MM.yyyy HH:mm") : "";
                    _chatSearchResultItems.Add(new SearchResultItem {
                        ChatId = _currentChatId, MessageId = fmId,
                        Subtitle = snippet, DateText = fmDateStr
                    });
                }
            }
            _chatSearchResultIndex = _chatSearchResultIds.Count > 0 ? 0 : -1;
            UpdateChatSearchCounter();
            ChatSearchResultsView.Visibility = Visibility.Visible;
        }

        private void UpdateChatSearchCounter() {
            ChatSearchCounter.Text = _chatSearchResultIds.Count == 0
                ? "0/0"
                : (_chatSearchResultIndex + 1) + "/" + _chatSearchResultIds.Count;
        }

        /// <summary>Тап по конкретному найденному сообщению в списке — прыгаем к нему и закрываем поиск.</summary>
        private void ChatSearchResult_ItemClick(object sender, ItemClickEventArgs e) {
            var item = e.ClickedItem as SearchResultItem;
            if (item == null) return;
            int idx = _chatSearchResultIds.IndexOf(item.MessageId);
            if (idx >= 0) _chatSearchResultIndex = idx;
            JumpToMessage(item.MessageId);
            ChatSearchBar.Visibility = Visibility.Collapsed;
            ChatSearchResultsView.Visibility = Visibility.Collapsed;
            ChatHeader.Visibility = Visibility.Visible;
        }

        // searchChatMessages отдаёт совпадения от новых к старым — "вниз" (⌄) переходит
        // к более новому совпадению, "вверх" (⌃) — к более старому.
        private void ChatSearchUp_Click(object sender, RoutedEventArgs e) {
            if (_chatSearchResultIds.Count == 0) return;
            _chatSearchResultIndex = (_chatSearchResultIndex + 1) % _chatSearchResultIds.Count;
            UpdateChatSearchCounter();
            JumpToMessage(_chatSearchResultIds[_chatSearchResultIndex]);
        }

        private void ChatSearchDown_Click(object sender, RoutedEventArgs e) {
            if (_chatSearchResultIds.Count == 0) return;
            _chatSearchResultIndex = (_chatSearchResultIndex - 1 + _chatSearchResultIds.Count) % _chatSearchResultIds.Count;
            UpdateChatSearchCounter();
            JumpToMessage(_chatSearchResultIds[_chatSearchResultIndex]);
        }

        /// <summary>
        /// Прыжок к сообщению по id — если оно уже среди загруженных, просто
        /// скроллим; если нет, запрашиваем окно истории вокруг него (тот же
        /// приём, что уже использовался для перехода к закреплённому
        /// сообщению — см. _pendingScrollToMsgId).
        /// </summary>
        private void JumpToMessage(long targetId) {
            if (targetId <= 0 || _currentChatId == 0) return;
            var loaded = _messageItems.FirstOrDefault(m => !m.IsSeparator && m.Id == targetId);
            if (loaded != null) {
                MessagesListView.ScrollIntoView(loaded, ScrollIntoViewAlignment.Leading);
            } else {
                _pendingScrollToMsgId = targetId;
                TdJson.SendUtf8(_client, "{\"@type\":\"getChatHistory\",\"chat_id\":" + _currentChatId +
                    ",\"from_message_id\":" + targetId + ",\"offset\":-10,\"limit\":20}");
            }
        }

        private void ChatHeaderProfile_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            if (_currentChatId == 0) return;
            ProfileOverlay.Visibility = Visibility.Visible;
            ProfileName.Text = CurrentChatTitle.Text;
            ProfileUsername.Visibility = Visibility.Collapsed;
            ProfilePhonePanel.Visibility = Visibility.Collapsed;
            ProfileBioPanel.Visibility = Visibility.Collapsed;
            ProfileMembersPanel.Visibility = Visibility.Collapsed;
            ProfileAvatarBrush.ImageSource = ChatHeaderAvatarBrush.ImageSource;
            ProfileAvatarPlaceholder.Fill = ChatHeaderAvatarBorder.Fill;
            ProfileAvatarInitials.Text = ChatHeaderAvatarInitials.Text;
            if (_rawChatsDict.ContainsKey(_currentChatId)) {
                var raw = _rawChatsDict[_currentChatId] as Newtonsoft.Json.Linq.JObject;
                long userId = raw?["type"]?["user_id"]?.ToObject<long>() ?? 0;
                long sgId3 = raw?["type"]?["supergroup_id"]?.ToObject<long>() ?? 0;
                long bgId3 = raw?["type"]?["basic_group_id"]?.ToObject<long>() ?? 0;
                if (userId != 0 && _usersDict.ContainsKey(userId)) {
                    // Приватный чат — показываем профиль пользователя
                    var u = _usersDict[userId];
                    string uname = u["username"]?.ToString() ?? u["usernames"]?["editable_username"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uname)) { ProfileUsername.Text = "@" + uname; ProfileUsername.Visibility = Visibility.Visible; }
                    string phone = u["phone_number"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(phone)) { ProfilePhone.Text = "+" + phone; ProfilePhonePanel.Visibility = Visibility.Visible; }
                    TdJson.SendUtf8(_client, "{\"@type\":\"getUserFullInfo\",\"user_id\":" + userId + "}");
                } else if (sgId3 != 0) {
                    // Супергруппа/канал
                    TdJson.SendUtf8(_client, "{\"@type\":\"getSupergroupFullInfo\",\"supergroup_id\":" + sgId3 + "}");
                    TdJson.SendUtf8(_client, "{\"@type\":\"getSupergroupMembers\",\"supergroup_id\":" + sgId3 + ",\"filter\":{\"@type\":\"supergroupMembersFilterRecent\"},\"offset\":0,\"limit\":50}");
                } else if (bgId3 != 0) {
                    // Базовая группа
                    TdJson.SendUtf8(_client, "{\"@type\":\"getBasicGroupFullInfo\",\"basic_group_id\":" + bgId3 + "}");
                }
            }
        }

        private void ProfileMember_Click(object sender, Windows.UI.Xaml.Controls.ItemClickEventArgs e) {
            var contact = e.ClickedItem as ContactItem;
            if (contact == null) return;
            ProfileOverlay.Visibility = Visibility.Collapsed;
            if (_chatsDict.ContainsKey(contact.UserId))
                OpenChat(_chatsDict[contact.UserId], 0);
            else
                TdJson.SendUtf8(_client, "{\"@type\":\"createPrivateChat\",\"user_id\":" + contact.UserId + ",\"force\":true}");
        }

        private void ShowProfileMembers(List<long> userIds) {
            if (ProfileMembersList == null) return;
            var members = new System.Collections.ObjectModel.ObservableCollection<ContactItem>();
            foreach (var uid in userIds) {
                if (!_usersDict.ContainsKey(uid)) continue;
                var u = _usersDict[uid];
                var ci = new ContactItem {
                    UserId = uid,
                    FullName = uid == _myUserId ? Loc.T("label_you") : ((u["first_name"]?.ToString() + " " + u["last_name"]?.ToString()).Trim()),
                    Username = u["username"]?.ToString() ?? u["usernames"]?["editable_username"]?.ToString() ?? "",
                    LastSeen = GetLastSeenText(u["status"])
                };
                members.Add(ci);
                if (_usersDict.ContainsKey(uid)) { var t = LoadContactAvatarFromUser(ci, u); }
            }
            ProfileMembersList.ItemsSource = members;
            ProfileMembersPanel.Visibility = members.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ProfileOverlay_Close(object sender, RoutedEventArgs e) {
            ProfileOverlay.Visibility = Visibility.Collapsed;
        }

        private void ProfileOverlay_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            // Закрываем при клике на фон
            if (e.OriginalSource == ProfileOverlay)
                ProfileOverlay.Visibility = Visibility.Collapsed;
        }

        private List<long> _forwardMessageIds = new List<long>();
        private long _forwardFromChatId = 0;








        private void ForwardChatOverlay_Close(object sender, RoutedEventArgs e) {
            ForwardChatOverlay.Visibility = Visibility.Collapsed;
            _forwardMessageIds.Clear();
        }

        private void ForwardChatList_ItemClick(object sender, ItemClickEventArgs e) {
            var chat = e.ClickedItem as ChatItem;
            if (chat == null || _forwardMessageIds.Count == 0) return;
            ForwardChatOverlay.Visibility = Visibility.Collapsed;
            string idsJson = "[" + string.Join(",", _forwardMessageIds) + "]";
            TdJson.SendUtf8(_client, "{\"@type\":\"forwardMessages\",\"chat_id\":" + chat.Id +
                ",\"from_chat_id\":" + _forwardFromChatId +
                ",\"message_ids\":" + idsJson +
                ",\"options\":{\"@type\":\"messageSendOptions\"}}");
            _forwardMessageIds.Clear();
        }

        private void StartBotButton_Click(object sender, RoutedEventArgs e) {
            if (_currentChatId == 0) return;
            StartBotButton.Visibility = Visibility.Collapsed;
            // Отправляем /start
            string req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                ",\"input_message_content\":{\"@type\":\"inputMessageText\"" +
                ",\"text\":{\"@type\":\"formattedText\",\"text\":\"/start\"}}}";
            TdJson.SendUtf8(_client, req);
        }

        private void AudioSlider_ManipulationStarted(object sender, Windows.UI.Xaml.Input.ManipulationStartedRoutedEventArgs e) {
            _audioSliderDragging = true;
        }
        private void AudioSlider_ManipulationCompleted(object sender, Windows.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e) {
            _audioSliderDragging = false;
            var slider = sender as Windows.UI.Xaml.Controls.Slider;
            if (slider == null) return;
            if (_currentAudioIsBass) {
                if (!BassPlayer.HasActiveStream) return;
                BassPlayer.Seek(TimeSpan.FromSeconds(slider.Value));
                return;
            }
            if (_currentAudioPlayer == null) return;
            _currentAudioPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(slider.Value);
        }

        /// <summary>Значок "💾" в пузыре аудио.</summary>
        private void SaveAudio_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Button;
            if (btn?.Tag == null) return;
            long msgId = (long)btn.Tag;
            if (!_messagesDict.ContainsKey(msgId)) return;
            var item = _messagesDict[msgId];
            if (!string.IsNullOrEmpty(item.FilePath)) {
                SaveAndToast(item.FilePath, Windows.Storage.Pickers.PickerLocationId.MusicLibrary);
            } else {
                // Аудио больше не качается автоматически при открытии чата —
                // запускаем загрузку сами и сохраним сразу по завершении.
                _pendingSaveMsgIds.Add(msgId);
                foreach (var kv in _audioFileIds) {
                    if (kv.Value == msgId) {
                        item.AudioPlayStatus = "⏳";
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":32,\"synchronous\":false}");
                        break;
                    }
                }
            }
        }

        private async void AudioButton_Click(object sender, RoutedEventArgs e) {
            var btn = sender as Button;
            long msgId = (long)btn.Tag;
            if (!_messagesDict.ContainsKey(msgId)) return;
            var item = _messagesDict[msgId];
            // Если уже играет — стоп
            if (_currentAudioMsgId == msgId && (_currentAudioPlayer != null || _currentAudioIsBass)) {
                StopCurrentAudio();
                item.AudioPlayStatus = "▶";
                return;
            }
            // Остановить предыдущий трек
            if (_currentAudioPlayer != null || _currentAudioIsBass) {
                if (_messagesDict.ContainsKey(_currentAudioMsgId))
                    _messagesDict[_currentAudioMsgId].AudioPlayStatus = "▶";
                StopCurrentAudio();
            }
            if (string.IsNullOrEmpty(item.FilePath)) {
                // Первый клик — запускаем загрузку (как и с видео), не проигрываем сразу
                foreach (var kv in _audioFileIds) {
                    if (kv.Value == msgId) {
                        item.AudioPlayStatus = "⏳";
                        TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + kv.Key + ",\"priority\":32,\"synchronous\":false}");
                        break;
                    }
                }
                return;
            }

            // .oga/.ogg (голосовые Telegram — Opus в Ogg) Media Foundation на
            // Windows 10 Mobile нативно не декодирует, поэтому для них отдельный
            // путь через BASS (из закреплённого в памяти буфера). Всё
            // остальное (mp3 и т.п.) — как раньше, через MediaPlayer.
            bool isOggVoice = item.FilePath.EndsWith(".oga", StringComparison.OrdinalIgnoreCase)
                            || item.FilePath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
            if (isOggVoice) {
                item.AudioPlayStatus = "⏳";
                bool bassOk = await BassPlayer.PlayAsync(item.FilePath);
                if (bassOk) {
                    AudioPlayerHost.Children.Clear();
                    _currentAudioIsBass = true;
                    _currentAudioMsgId = msgId;
                    item.AudioPlayStatus = "⏹";
                    _currentAudioFilePath = item.FilePath;
                    _currentAudioPosition = TimeSpan.Zero;
                    var lenNow = BassPlayer.GetLength();
                    if (lenNow.TotalSeconds > 0) item.AudioDurationSeconds = lenNow.TotalSeconds;
                } else {
                    // bass.dll/bassopus.dll не найдены или не смогли открыть файл —
                    // тихо возвращаем кнопку в исходное состояние, ничего не играет.
                    item.AudioPlayStatus = "▶";
                }
                return;
            }

            try {
                var player = new Windows.Media.Playback.MediaPlayer();
                player.AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media;
                var source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.FilePath));
                _currentAudioSource = source;
                player.Source = source;

                SetupPlayer(player, item, TimeSpan.Zero);

                player.Play();
                AudioPlayerHost.Children.Clear();
                _currentAudioPlayer = player;
                _currentAudioMsgId = msgId;
                item.AudioPlayStatus = "⏹";
                _currentAudioFilePath = item.FilePath;
                _currentAudioPosition = TimeSpan.Zero;
                await RequestMediaSessionAsync();
            } catch { }
        }

        /// <summary>Останавливает текущий трек независимо от того, MediaPlayer это или BASS.</summary>
        private void StopCurrentAudio() {
            if (_currentAudioIsBass) {
                BassPlayer.Stop();
                _currentAudioIsBass = false;
            } else if (_currentAudioPlayer != null) {
                _currentAudioPlayer.Pause();
                _currentAudioPlayer.Source = null;
                _currentAudioPlayer.SystemMediaTransportControls.PlaybackStatus = Windows.Media.MediaPlaybackStatus.Stopped;
                ReleaseMediaSession();
            }
            _currentAudioPlayer = null;
            _currentAudioSource = null;
            _currentAudioMsgId = 0;
            _currentAudioFilePath = null;
        }

        // Настройка SMTC и обработчиков событий плеера. Вызывается и при старте, и при восстановлении после suspend.
        private void SetupPlayer(Windows.Media.Playback.MediaPlayer player, MessageItem item, TimeSpan startPosition) {
            var smtc = player.SystemMediaTransportControls;
            smtc.IsEnabled = true;
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = false;
            smtc.IsNextEnabled = false;
            smtc.IsPreviousEnabled = false;
            smtc.DisplayUpdater.Type = Windows.Media.MediaPlaybackType.Music;
            smtc.DisplayUpdater.MusicProperties.Title = item.AudioTitle ?? "";
            smtc.DisplayUpdater.Update();
            smtc.PlaybackPositionChangeRequested += (ss, ee) => {
                player.PlaybackSession.Position = ee.RequestedPlaybackPosition;
            };
            player.PlaybackSession.PositionChanged += (session, args) => {
                smtc.UpdateTimelineProperties(new Windows.Media.SystemMediaTransportControlsTimelineProperties {
                    StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero,
                    Position = session.Position,
                    MaxSeekTime = session.NaturalDuration,
                    EndTime = session.NaturalDuration
                });
            };
            player.PlaybackSession.PlaybackStateChanged += (session, args) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing)
                        item.AudioPlayStatus = "⏹";
                    else if (session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Paused)
                        item.AudioPlayStatus = "▶";
                });
            };
            player.MediaOpened += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    if (startPosition > TimeSpan.Zero)
                        player.PlaybackSession.Position = startPosition;
                    var dur = player.PlaybackSession.NaturalDuration;
                    if (dur.TotalSeconds > 0) item.AudioDurationSeconds = dur.TotalSeconds;
                });
            };
            player.MediaEnded += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    item.AudioPlayStatus = "▶";
                    _currentAudioPlayer = null; _currentAudioSource = null;
                    _currentAudioMsgId = 0; _currentAudioFilePath = null;
                    ReleaseMediaSession();
                });
            };
            player.MediaFailed += (s, ev) => {
                var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    item.AudioPlayStatus = "▶";
                    _currentAudioPlayer = null; _currentAudioSource = null;
                    _currentAudioMsgId = 0;
                    // НЕ сбрасываем _currentAudioFilePath — нужен для восстановления в Resuming
                    ReleaseMediaSession();
                });
            };
        }

        private async void MicButton_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (_currentChatId == 0 || _isRecording) return;
            try {
                _mediaCapture = new Windows.Media.Capture.MediaCapture();
                await _mediaCapture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings {
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.Audio
                });
                if (_filesFolder == null) { Log("MIC ERR _filesFolder is null!"); return; }
                string fname = "voice_" + DateTimeOffset.Now.ToUnixTimeSeconds() + ".m4a";
                _recordingFile = await _filesFolder.CreateFileAsync(fname, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateM4a(
                    Windows.Media.MediaProperties.AudioEncodingQuality.Medium);
                await _mediaCapture.StartRecordToStorageFileAsync(profile, _recordingFile);
                _isRecording = true;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 50, 50));
            } catch {
                _mediaCapture?.Dispose();
                _mediaCapture = null;
            }
        }

        private async void MicButton_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (!_isRecording || _mediaCapture == null) return;
            try {
                await _mediaCapture.StopRecordAsync();
                _isRecording = false;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
                _mediaCapture.Dispose();
                _mediaCapture = null;
                var props = await _recordingFile.Properties.GetMusicPropertiesAsync();
                int durationSec = (int)props.Duration.TotalSeconds;
                string voicePath = _recordingFile.Path.Replace("\\", "/");
                string voiceReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId + ThreadJsonPart() +
                    ",\"input_message_content\":{\"@type\":\"inputMessageVoiceNote\"" +
                    ",\"voice_note\":{\"@type\":\"inputVoiceNote\"" +
                    ",\"voice_note\":{\"@type\":\"inputFileLocal\",\"path\":\"" + voicePath.Replace("\"","\\\"") + "\"}" +
                    ",\"duration\":" + durationSec +
                    ",\"waveform\":\"\"}" +
                    ",\"caption\":{\"@type\":\"formattedText\",\"text\":\"\"}}}";
                TdJson.SendUtf8(_client, voiceReq);
            } catch {
                _isRecording = false;
                MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            }
        }

        private bool _isRecordingVideoNote = false;

        private async void VideoNoteButton_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (_currentChatId == 0 || _isRecordingVideoNote) return;
            try {
                _isRecordingVideoNote = true;
                _videoCaptureCapture = new Windows.Media.Capture.MediaCapture();
                await _videoCaptureCapture.InitializeAsync(new Windows.Media.Capture.MediaCaptureInitializationSettings {
                    StreamingCaptureMode = Windows.Media.Capture.StreamingCaptureMode.AudioAndVideo,
                    VideoDeviceId = await GetFrontCameraId()
                });
                // Поворачиваем на 90° для портретной ориентации
                var props = _videoCaptureCapture.VideoDeviceController.GetMediaStreamProperties(
                    Windows.Media.Capture.MediaStreamType.VideoRecord) as Windows.Media.MediaProperties.VideoEncodingProperties;
                if (props != null) {
                    System.Guid rotGuid = new System.Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
                    props.Properties.Add(rotGuid, 270);
                    await _videoCaptureCapture.VideoDeviceController.SetMediaStreamPropertiesAsync(
                        Windows.Media.Capture.MediaStreamType.VideoRecord, props);
                }
                VideoNotePreview.Source = _videoCaptureCapture;
                await _videoCaptureCapture.StartPreviewAsync();
                VideoNoteOverlay.Visibility = Visibility.Visible;
                // Создаём файл
                string fname = "vidnote_" + Environment.TickCount + ".mp4";
                _videoNoteFile = await _filesFolder.CreateFileAsync(fname, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                var profile = Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(
                    Windows.Media.MediaProperties.VideoEncodingQuality.Auto);
                await _videoCaptureCapture.StartRecordToStorageFileAsync(profile, _videoNoteFile);
                // Таймер
                _videoNoteSeconds = 0;
                VideoNoteTimer.Text = "0:00";
                _videoNoteTimer = new Windows.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _videoNoteTimer.Tick += (ts, te) => {
                    _videoNoteSeconds++;
                    VideoNoteTimer.Text = _videoNoteSeconds / 60 + ":" + (_videoNoteSeconds % 60).ToString("D2");
                    if (_videoNoteSeconds >= MaxVideoNoteSeconds)
                        VideoNoteButton_PointerReleased(null, null);
                };
                _videoNoteTimer.Start();
            } catch {
                _isRecordingVideoNote = false;
                VideoNoteOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async void VideoNoteButton_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (!_isRecordingVideoNote || _videoCaptureCapture == null) return;
            try {
                _videoNoteTimer?.Stop();
                _videoNoteTimer = null;
                await _videoCaptureCapture.StopRecordAsync();
                await _videoCaptureCapture.StopPreviewAsync();
                _isRecordingVideoNote = false;
                VideoNotePreview.Source = null;
                VideoNoteOverlay.Visibility = Visibility.Collapsed;
                _videoCaptureCapture.Dispose();
                _videoCaptureCapture = null;
                if (_videoNoteSeconds < 1) return; // слишком короткое
                string path = _videoNoteFile.Path.Replace("\\", "/");
                string req = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId + ThreadJsonPart() +
                    ",\"input_message_content\":{\"@type\":\"inputMessageVideoNote\"" +
                    ",\"video_note\":{\"@type\":\"inputVideoNote\"" +
                    ",\"video_note\":{\"@type\":\"inputFileLocal\",\"path\":\"" + path.Replace("\"","\\\"") + "\"}" +
                    ",\"duration\":" + _videoNoteSeconds +
                    ",\"length\":240}}}";
                TdJson.SendUtf8(_client, req);
            } catch {
                _isRecordingVideoNote = false;
                VideoNoteOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async System.Threading.Tasks.Task<string> GetFrontCameraId() {
            var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(
                Windows.Devices.Enumeration.DeviceClass.VideoCapture);
            foreach (var d in devices)
                if (d.EnclosureLocation?.Panel == Windows.Devices.Enumeration.Panel.Front)
                    return d.Id;
            return devices.Count > 0 ? devices[0].Id : "";
        }

        private void ChatItem_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            var grid = sender as Grid;
            if (grid == null) return;
            var chat = grid.DataContext as ChatItem;
            if (chat == null) return;
            _pendingDeleteChatId = chat.Id;
            // Меняем текст пунктов меню по состоянию чата
            var flyout = FlyoutBase.GetAttachedFlyout(grid) as MenuFlyout;
            if (flyout != null) {
                bool isInArchive = _archiveChatIds.Contains(chat.Id);
                bool isPinned = chat.IsPinned;
                foreach (var fi in flyout.Items.OfType<MenuFlyoutItem>()) {
                    if (fi.Name == "MenuArchiveChat")
                        fi.Text = isInArchive ? Loc.T("chatmenu_unarchive") : Loc.T("chatmenu_archive");
                    if (fi.Name == "MenuPinChat")
                        fi.Text = isPinned ? Loc.T("msgmenu_unpin") : Loc.T("msgmenu_pin");
                    if (fi.Name == "MenuMarkUnread")
                        fi.Text = chat.IsMarkedUnread ? Loc.T("chatmenu_read") : Loc.T("chatmenu_unread");
                    if (fi.Name == "MenuMuteChat")
                        fi.Text = chat.IsMuted ? Loc.T("chatmenu_unmute") : Loc.T("chatmenu_mute");
                    if (fi.Name == "MenuMarkRead")
                        fi.Visibility = (chat.UnreadCount > 0 || chat.IsMarkedUnread) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            Windows.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(grid);
        }

        private void MarkUnread_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            if (!_chatsDict.ContainsKey(chatId)) return;
            bool newMarked = !_chatsDict[chatId].IsMarkedUnread;
            var req = "{\"@type\":\"toggleChatIsMarkedAsUnread\",\"chat_id\":" + chatId + ",\"is_marked_as_unread\":" + (newMarked ? "true" : "false") + "}";
            TdJson.SendUtf8(_client, req);
        }

        /// <summary>
        /// Вкл/выкл уведомлений для чата/группы/канала — статус хранится на
        /// сервере Telegram (setChatNotificationSettings), а не только локально,
        /// поэтому синхронизируется между устройствами так же, как в оригинале.
        /// </summary>
        /// <summary>
        /// Помечает чат прочитанным на сервере: viewMessages с force_read по
        /// последнему сообщению закрывает реальные непрочитанные, а если чат
        /// был отмечен непрочитанным вручную — снимаем и эту пометку отдельно
        /// (это два независимых флага в TDLib).
        /// </summary>
        private void MarkChatRead_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            if (!_chatsDict.ContainsKey(chatId)) return;

            if (_rawChatsDict.ContainsKey(chatId)) {
                var raw = _rawChatsDict[chatId] as JObject;
                long lastMsgId = raw?["last_message"]?["id"]?.ToObject<long>() ?? 0;
                if (lastMsgId != 0) {
                    TdJson.SendUtf8(_client, "{\"@type\":\"viewMessages\",\"chat_id\":" + chatId +
                        ",\"message_ids\":[" + lastMsgId + "],\"force_read\":true}");
                }
            }
            if (_chatsDict[chatId].IsMarkedUnread) {
                TdJson.SendUtf8(_client, "{\"@type\":\"toggleChatIsMarkedAsUnread\",\"chat_id\":" + chatId + ",\"is_marked_as_unread\":false}");
            }
            // Обновляем сразу, не дожидаясь updateChatReadInbox/updateChatIsMarkedAsUnread
            _chatsDict[chatId].UnreadCount = 0;
            _chatsDict[chatId].IsMarkedUnread = false;
            UpdateArchiveUnreadBadge();
        }

        private void MuteChat_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            if (!_chatsDict.ContainsKey(chatId)) return;
            bool newMuted = !_chatsDict[chatId].IsMuted;
            // 2147483647 (int32 max) — тот же способ "замьютить навсегда", что
            // используют официальные клиенты; 0 — снять мьют.
            int muteFor = newMuted ? int.MaxValue : 0;
            var req = new JObject {
                ["@type"] = "setChatNotificationSettings",
                ["chat_id"] = chatId,
                ["notification_settings"] = new JObject {
                    ["@type"] = "chatNotificationSettings",
                    ["use_default_mute_for"] = false,
                    ["mute_for"] = muteFor,
                    ["use_default_sound"] = true,
                    ["use_default_show_preview"] = true,
                    ["use_default_mute_stories"] = true,
                    ["use_default_show_story_sender"] = true,
                    ["use_default_disable_pinned_message_notifications"] = true,
                    ["use_default_disable_mention_notifications"] = true
                }
            };
            TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
            // Не ждём updateChatNotificationSettings — обновляем сразу для
            // мгновенного отклика в UI (сервер всё равно пришлёт подтверждение).
            _chatsDict[chatId].IsMuted = newMuted;
        }

        private void PinChat_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            if (!_chatsDict.ContainsKey(chatId)) return;
            bool newPinned = !_chatsDict[chatId].IsPinned;
            string listType = _archiveChatIds.Contains(chatId) ? "chatListArchive" : "chatListMain";
            var req = new JObject {
                ["@type"] = "toggleChatIsPinned",
                ["chat_list"] = new JObject { ["@type"] = listType },
                ["chat_id"] = chatId,
                ["is_pinned"] = newPinned
            };
            string reqStr = req.ToString(Newtonsoft.Json.Formatting.None);
            TdJson.SendUtf8(_client, reqStr);
        }

        private void ArchiveChat_Click(object sender, RoutedEventArgs e) {
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            bool isInArchive = _archiveChatIds.Contains(chatId);
            string targetList = isInArchive ? "chatListMain" : "chatListArchive";
            var req = "{\"@type\":\"addChatToList\",\"chat_id\":" + chatId + ",\"chat_list\":{\"@type\":\"" + targetList + "\"}}";
            TdJson.SendUtf8(_client, req);
        }

        private async void DeleteChat_Click(object sender, RoutedEventArgs e) {
            var item = sender as MenuFlyoutItem;
            // Ищем Tag через визуальное дерево — идём вверх от MenuFlyoutItem
            // Tag был установлен на Grid в ChatItem_Holding
            // Ищем чат через _chatsDict по совпадению с открытым flyout
            // Надёжнее хранить pending id отдельно
            if (_pendingDeleteChatId == 0) return;
            long chatId = _pendingDeleteChatId;
            _pendingDeleteChatId = 0;
            // Показываем диалог подтверждения
            var dialog = new Windows.UI.Popups.MessageDialog(Loc.T("dlg_deleteChat_body"), Loc.T("dlg_deleteChat_title"));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_delete"), async cmd => {
                var req = Newtonsoft.Json.Linq.JObject.FromObject(new {
                    type = "deleteChatHistory",
                    chat_id = chatId,
                    remove_from_chat_list = true,
                    revoke = false
                });
                req["@type"] = req["type"]; req.Remove("type");
                TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
                // Убираем из списка
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    var toRemove = _chatListItems.FirstOrDefault(c => c.Id == chatId);
                    if (toRemove != null) _chatListItems.Remove(toRemove);
                    _allChatItems.RemoveAll(c => c.Id == chatId);
                    if (_chatsDict.ContainsKey(chatId)) _chatsDict.Remove(chatId);
                    if (_pendingPinnedPositions.ContainsKey(chatId)) _pendingPinnedPositions.Remove(chatId);
                    // Удаляем из папок
                    foreach (var fl in _folderChatIds.Values) fl.Remove(chatId);
                    ChatCountText.Text = _chatListItems.Count.ToString();
                });
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_cancel")));
            await dialog.ShowAsync();
        }

        private void ApplySavedProxy() {
            var s = Windows.Storage.ApplicationData.Current.LocalSettings;
            switch (_proxyMode) {
                case ProxyMode.None:
                    TdJson.SendUtf8(_client, "{\"@type\":\"disableProxy\"}");
                    break;
                case ProxyMode.Auto:
                    var t = FetchAndApplyProxyAsync();
                    break;
                case ProxyMode.Mtproto: {
                    string host   = s.Values.ContainsKey("proxy_mtp_host")   ? (string)s.Values["proxy_mtp_host"]   : "";
                    string port   = s.Values.ContainsKey("proxy_mtp_port")   ? (string)s.Values["proxy_mtp_port"]   : "";
                    string secret = s.Values.ContainsKey("proxy_mtp_secret") ? (string)s.Values["proxy_mtp_secret"] : "";
                    if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && !string.IsNullOrEmpty(secret) && int.TryParse(port, out int p)) {
                        var t2 = ApplyProxyAsync(host, p, secret);
                    }
                    break;
                }
                case ProxyMode.Http: {
                    string host = s.Values.ContainsKey("proxy_http_host") ? (string)s.Values["proxy_http_host"] : "";
                    string port = s.Values.ContainsKey("proxy_http_port") ? (string)s.Values["proxy_http_port"] : "";
                    if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && int.TryParse(port, out int p)) {
                        ClearAllProxies();
                        string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                                     "\",\"port\":" + p + ",\"type\":{\"@type\":\"proxyTypeHttp\",\"username\":\"\",\"password\":\"\",\"http_only\":false}},\"enable\":true}";
                        TdJson.SendUtf8(_client, req);
                        ProxyStatusText.Text = "[..] " + host + ":" + p;
                        ProxyStatusText.Visibility = Visibility.Visible;
                    }
                    break;
                }
                case ProxyMode.Socks: {
                    string host = s.Values.ContainsKey("proxy_socks_host") ? (string)s.Values["proxy_socks_host"] : "";
                    string port = s.Values.ContainsKey("proxy_socks_port") ? (string)s.Values["proxy_socks_port"] : "";
                    string user = s.Values.ContainsKey("proxy_socks_user") ? (string)s.Values["proxy_socks_user"] : "";
                    string pass = s.Values.ContainsKey("proxy_socks_pass") ? (string)s.Values["proxy_socks_pass"] : "";
                    if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port) && int.TryParse(port, out int p)) {
                        ClearAllProxies();
                        string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                                     "\",\"port\":" + p + ",\"type\":{\"@type\":\"proxyTypeSocks5\",\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}},\"enable\":true}";
                        TdJson.SendUtf8(_client, req);
                        ProxyStatusText.Text = "[..] " + host + ":" + p;
                        ProxyStatusText.Visibility = Visibility.Visible;
                    }
                    break;
                }
            }
        }

        private void SaveProxySettings() {
            try {
                var s = Windows.Storage.ApplicationData.Current.LocalSettings;
                s.Values["proxy_mode"] = (int)_proxyMode;
                s.Values["proxy_mtp_host"]   = MtpHost.Text.Trim();
                s.Values["proxy_mtp_port"]   = MtpPort.Text.Trim();
                s.Values["proxy_mtp_secret"] = MtpSecret.Text.Trim();
                s.Values["proxy_http_host"]  = HttpHost.Text.Trim();
                s.Values["proxy_http_port"]  = HttpPort.Text.Trim();
                s.Values["proxy_socks_host"] = SocksHost.Text.Trim();
                s.Values["proxy_socks_port"] = SocksPort.Text.Trim();
                s.Values["proxy_socks_user"] = SocksUser.Text.Trim();
                s.Values["proxy_socks_pass"] = SocksPass.Password;
            } catch { }
        }

        private void LoadProxySettings() {
            var s = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (s.Values.ContainsKey("proxy_mode"))
                _proxyMode = (ProxyMode)(int)s.Values["proxy_mode"];
        }

        private void LoadProxySettingsToUI() {
            // Вызывается только при открытии попапа — UI элементы гарантированно существуют
            var s = Windows.Storage.ApplicationData.Current.LocalSettings;
            if (s.Values.ContainsKey("proxy_mtp_host"))   MtpHost.Text   = (string)s.Values["proxy_mtp_host"];
            if (s.Values.ContainsKey("proxy_mtp_port"))   MtpPort.Text   = (string)s.Values["proxy_mtp_port"];
            if (s.Values.ContainsKey("proxy_mtp_secret")) MtpSecret.Text = (string)s.Values["proxy_mtp_secret"];
            if (s.Values.ContainsKey("proxy_http_host"))  HttpHost.Text  = (string)s.Values["proxy_http_host"];
            if (s.Values.ContainsKey("proxy_http_port"))  HttpPort.Text  = (string)s.Values["proxy_http_port"];
            if (s.Values.ContainsKey("proxy_socks_host")) SocksHost.Text  = (string)s.Values["proxy_socks_host"];
            if (s.Values.ContainsKey("proxy_socks_port")) SocksPort.Text  = (string)s.Values["proxy_socks_port"];
            if (s.Values.ContainsKey("proxy_socks_user")) SocksUser.Text  = (string)s.Values["proxy_socks_user"];
            if (s.Values.ContainsKey("proxy_socks_pass")) SocksPass.Password = (string)s.Values["proxy_socks_pass"];
        }

        private void ProxySettingsButton_Click(object sender, RoutedEventArgs e) {
            // Загружаем поля из LocalSettings
            LoadProxySettingsToUI();
            // Выставляем текущий режим в UI
            ProxyModeNone.IsChecked     = _proxyMode == ProxyMode.None;
            ProxyModeAuto.IsChecked     = _proxyMode == ProxyMode.Auto;
            ProxyModeMtproto.IsChecked  = _proxyMode == ProxyMode.Mtproto;
            ProxyModeHttp.IsChecked     = _proxyMode == ProxyMode.Http;
            ProxyModeSocks.IsChecked    = _proxyMode == ProxyMode.Socks;
            UpdateProxyFields();
            // Центрируем popup
            ProxyPopup.HorizontalOffset = (ActualWidth - 320) / 2;
            ProxyPopup.VerticalOffset   = (ActualHeight - 400) / 2;
            ProxyPopup.IsOpen = true;
        }

        private void ProxyMode_Checked(object sender, RoutedEventArgs e) {
            UpdateProxyFields();
        }

        private void UpdateProxyFields() {
            if (MtprotoFields == null) return;
            MtprotoFields.Visibility = (ProxyModeMtproto?.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            HttpFields.Visibility    = (ProxyModeHttp?.IsChecked    == true) ? Visibility.Visible : Visibility.Collapsed;
            SocksFields.Visibility   = (ProxyModeSocks?.IsChecked   == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ProxyCancel_Click(object sender, RoutedEventArgs e) {
            ProxyPopup.IsOpen = false;
        }

        private void ProxyApply_Click(object sender, RoutedEventArgs e) {
            ProxyPopup.IsOpen = false;
            // Сначала обновляем _proxyMode, потом сохраняем
            if (ProxyModeNone.IsChecked == true)         _proxyMode = ProxyMode.None;
            else if (ProxyModeAuto.IsChecked == true)    _proxyMode = ProxyMode.Auto;
            else if (ProxyModeMtproto.IsChecked == true) _proxyMode = ProxyMode.Mtproto;
            else if (ProxyModeHttp.IsChecked == true)    _proxyMode = ProxyMode.Http;
            else if (ProxyModeSocks.IsChecked == true)   _proxyMode = ProxyMode.Socks;
            SaveProxySettings();

            if (_proxyMode == ProxyMode.None) {
                _proxyApplied = true;
                TdJson.SendUtf8(_client, "{\"@type\":\"disableProxy\"}");
                ProxyStatusText.Text = Loc.T("proxy_status_none");
                ProxyStatusText.Visibility = Visibility.Visible;
            } else if (_proxyMode == ProxyMode.Auto) {
                _proxyApplied = false;
                _proxyList.Clear();
                _proxyIndex = 0;
                var t = FetchAndApplyProxyAsync();
            } else if (_proxyMode == ProxyMode.Mtproto) {
                string host = MtpHost.Text.Trim();
                string portStr = MtpPort.Text.Trim();
                string secret = MtpSecret.Text.Trim();
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr) || string.IsNullOrEmpty(secret)) {
                    LoginStatus.Text = Loc.T("login_fillMtproto");
                    return;
                }
                if (!int.TryParse(portStr, out int port)) {
                    LoginStatus.Text = Loc.T("login_wrongPort");
                    return;
                }
                _proxyApplied = true;
                var t = ApplyProxyAsync(host, port, secret);
            } else if (_proxyMode == ProxyMode.Http) {
                string host = HttpHost.Text.Trim();
                string portStr = HttpPort.Text.Trim();
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr)) {
                    LoginStatus.Text = Loc.T("login_fillHttp");
                    return;
                }
                if (!int.TryParse(portStr, out int port)) {
                    LoginStatus.Text = Loc.T("login_wrongPort");
                    return;
                }
                _proxyApplied = true;
                ClearAllProxies();
                string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                             "\",\"port\":" + port + ",\"type\":{\"@type\":\"proxyTypeHttp\",\"username\":\"\",\"password\":\"\",\"http_only\":false}},\"enable\":true}";
                TdJson.SendUtf8(_client, req);
                ProxyStatusText.Text = "[..] " + host + ":" + port;
                ProxyStatusText.Visibility = Visibility.Visible;
            } else if (_proxyMode == ProxyMode.Socks) {
                string host = SocksHost.Text.Trim();
                string portStr = SocksPort.Text.Trim();
                string user = SocksUser.Text.Trim();
                string pass = SocksPass.Password;
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(portStr)) {
                    LoginStatus.Text = Loc.T("login_fillSocks");
                    return;
                }
                if (!int.TryParse(portStr, out int port)) {
                    LoginStatus.Text = Loc.T("login_wrongPort");
                    return;
                }
                _proxyApplied = true;
                ClearAllProxies();
                string req = "{\"@type\":\"addProxy\",\"proxy\":{\"@type\":\"proxy\",\"server\":\"" + host +
                             "\",\"port\":" + port + ",\"type\":{\"@type\":\"proxyTypeSocks5\",\"username\":\"" + user + "\",\"password\":\"" + pass + "\"}},\"enable\":true}";
                TdJson.SendUtf8(_client, req);
                ProxyStatusText.Text = "[..] " + host + ":" + port;
                ProxyStatusText.Visibility = Visibility.Visible;
            }
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e) {
            _isLightTheme = !_isLightTheme;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["light_theme"] = _isLightTheme;
            ApplyTheme();
        }

        private void ApplyTheme() {
            if (_isLightTheme) ApplyLightTheme();
            else ApplyDarkTheme();
            // ListView.Header рендерится асинхронно — применяем ещё раз через 200мс
            var t = new Windows.UI.Xaml.DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(200);
            t.Tick += (s2, e2) => {
                t.Stop();
                if (_isLightTheme) ApplyLightTheme();
                else ApplyDarkTheme();
            };
            t.Start();
        }

        private static Windows.UI.Xaml.Media.SolidColorBrush CB(string hex) {
            hex = hex.TrimStart('#');
            byte a = 255, r, g, b;
            if (hex.Length == 8) { a = Convert.ToByte(hex.Substring(0,2),16); hex = hex.Substring(2); }
            r = Convert.ToByte(hex.Substring(0,2),16);
            g = Convert.ToByte(hex.Substring(2,2),16);
            b = Convert.ToByte(hex.Substring(4,2),16);
            return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(a,r,g,b));
        }

        private void ApplyDarkTheme() {
            ThemeToggleButton.Content = "☀";
            BubbleColorOut = "#2B5278";
            BubbleColorIn  = "#182533";
            ChatItem.ThemeTitleColor    = "#FFFFFF";
            ChatItem.ThemeSubtitleColor = "#888888";
            ChatItem.ThemeTimeColor     = "#888888";
            ChatItem.ThemeStatusColor   = "#0088cc";
            StartPanel.Background          = CB("#111111");
            MessagesPanel.Background       = CB("#111111");
            ChatHeader.Background          = CB("#1F3A52");
            ChatSearchBar.Background       = CB("#1F3A52");
            ChatSearchResultsView.Background = CB("#111111");
            ChatSearchButton.Foreground     = CB("#FFFFFF");
            ChatSearchBox.Foreground        = CB("#FFFFFF");
            ChatSearchCounter.Foreground    = CB("#CCE8FF");
            ChatSearchUpButton.Foreground   = CB("#FFFFFF");
            ChatSearchDownButton.Foreground = CB("#FFFFFF");
            ChatSearchCloseButton.Foreground = CB("#FFFFFF");
            BackButton.Foreground          = CB("#FFFFFF");
            CurrentChatTitle.Foreground    = CB("#FFFFFF");
            CurrentChatStatus.Foreground   = CB("#CCE8FF");
            if (ArchiveBackButton != null) ArchiveBackButton.Foreground = CB("#FFFFFF");
            if (ArchiveTitleText  != null) ArchiveTitleText.Foreground  = CB("#FFFFFF");
            if (ArchiveRowTitle   != null) ArchiveRowTitle.Foreground   = CB("#FFFFFF");
            ArchiveSubtitleText.Foreground = CB("#888888");
            InputPanel.Background          = CB("#1A1A1A");
            InputBorder.Background         = CB("#222222");
            if (MessageInputBorder != null) {
                MessageInputBorder.Background    = CB("#2A2A2A");
                MessageInputBorder.BorderBrush   = CB("#444444");
                MessageInputBorder.BorderThickness = new Windows.UI.Xaml.Thickness(1);
            }
            MessageInput.Foreground            = CB("#FFFFFF");
            AttachMenuButton.Foreground        = CB("#FFFFFF");
            var hdr = ChatListView.Header as Windows.UI.Xaml.Controls.StackPanel;
            if (hdr != null) hdr.Background = CB("#1A1A1A");
            ArchiveRow.Background          = CB("#222222");
            UnogramTitle.Foreground        = CB("#FFFFFF");
            ChatCountText.Foreground       = CB("#888888");
            ArchiveChatCountText.Foreground = CB("#888888");
            ThemeToggleButton.Foreground   = CB("#888888");
            ProxyStatusText.Foreground     = CB("#555555");
            ProxySettingsButton.Background  = CB("#AA333333");
            ProxySettingsButton.Foreground  = CB("#AAAAAA");
            // Поле поиска — тёмная тема
            if (SearchPanel != null) SearchPanel.Background = CB("#1C1C1E");
            if (SearchBorder != null) SearchBorder.Background = CB("#2A2A2E");
            if (SearchBox != null) SearchBox.Foreground = CB("#FFFFFF");
            if (FolderTabsScroll != null) FolderTabsScroll.Background = CB("#1C1C1E");
            UpdateFolderTabStyles();
            // Закреплённое — тёмная тема
            if (PinnedMessageBar != null) {
                PinnedMessageBar.Background = CB("#CC1F3A52");
                PinnedMessageText.Foreground = CB("#FFFFFF");
                PinnedLabel.Foreground = CB("#2AABEE");
                PinnedAccentLine.Fill = CB("#2AABEE");
            }
            NotifyAllChatTheme();
            UpdateBubbleColors();
        }

        private void ApplyLightTheme() {
            ThemeToggleButton.Content = "🌙";
            BubbleColorOut = "#EFFDDE";
            BubbleColorIn  = "#FFFFFF";
            // Статические цвета для DataTemplate чатов
            ChatItem.ThemeTitleColor    = "#000000";
            ChatItem.ThemeSubtitleColor = "#707070";
            ChatItem.ThemeTimeColor     = "#707070";
            ChatItem.ThemeStatusColor   = "#4CAF50";
            // Фон
            StartPanel.Background          = CB("#EFEFF3");
            MessagesPanel.Background       = CB("#B2CDB0");
            // Шапка чата — белая
            ChatHeader.Background          = CB("#FFFFFF");
            BackButton.Foreground          = CB("#2AABEE");  // синяя стрелка назад
            CurrentChatTitle.Foreground    = CB("#000000");  // чёрный ник
            CurrentChatStatus.Foreground   = CB("#000000");  // тёмно-серый статус
            ChatSearchButton.Foreground    = CB("#2AABEE");
            ChatSearchBar.Background       = CB("#FFFFFF");
            ChatSearchResultsView.Background = CB("#FFFFFF");
            ChatSearchBox.Foreground       = CB("#000000");
            ChatSearchCounter.Foreground   = CB("#000000");
            ChatSearchUpButton.Foreground  = CB("#2AABEE");
            ChatSearchDownButton.Foreground = CB("#2AABEE");
            ChatSearchCloseButton.Foreground = CB("#2AABEE");
            // Архив
            if (ArchiveBackButton != null) ArchiveBackButton.Foreground = CB("#2AABEE");
            if (ArchiveTitleText  != null) ArchiveTitleText.Foreground  = CB("#000000");
            if (ArchiveRowTitle   != null) ArchiveRowTitle.Foreground   = CB("#000000");
            ArchiveSubtitleText.Foreground = CB("#707070");
            // Панель ввода — светло-серая
            InputPanel.Background          = CB("#F4F4F5");
            InputBorder.Background         = CB("#F4F4F5");
            if (MessageInputBorder != null) {
                MessageInputBorder.Background    = CB("#F0F2F5");
                MessageInputBorder.BorderBrush   = CB("#D8DCE0");
                MessageInputBorder.BorderThickness = new Windows.UI.Xaml.Thickness(1);
            }
            MessageInput.Foreground            = CB("#000000");
            AttachMenuButton.Foreground        = CB("#000000");
            var hdr = ChatListView.Header as Windows.UI.Xaml.Controls.StackPanel;
            if (hdr != null) hdr.Background = CB("#FFFFFF");
            ArchiveRow.Background          = CB("#F0F0F0");
            UnogramTitle.Foreground        = CB("#000000");
            ChatCountText.Foreground       = CB("#707070");
            ArchiveChatCountText.Foreground = CB("#707070");
            ThemeToggleButton.Foreground   = CB("#707070");
            ProxyStatusText.Foreground     = CB("#707070");
            ProxySettingsButton.Background  = CB("#E5E5E5");
            ProxySettingsButton.Foreground  = CB("#555555");
            // Поле поиска — светлая тема
            if (SearchPanel != null) SearchPanel.Background = CB("#EFEFF3");
            if (SearchBorder != null) SearchBorder.Background = CB("#E0E0E5");
            if (SearchBox != null) SearchBox.Foreground = CB("#000000");
            // Вкладки папок — светлый фон
            if (FolderTabsScroll != null) FolderTabsScroll.Background = CB("#FFFFFF");
            UpdateFolderTabStyles();
            // Закреплённое — светлая тема как в оригинальном Telegram
            if (PinnedMessageBar != null) {
                PinnedMessageBar.Background = CB("#FFFFFF");
                PinnedMessageText.Foreground = CB("#222222");
                PinnedLabel.Foreground = CB("#2AABEE");
                PinnedAccentLine.Fill = CB("#2AABEE");
            }
            NotifyAllChatTheme();
            UpdateBubbleColors();
        }

        private void BuildFolderTabs(Newtonsoft.Json.Linq.JArray folders) {
            FolderTabs.Children.Clear();
            if (folders == null || folders.Count == 0) {
                FolderTabsScroll.Visibility = Visibility.Collapsed;
                return;
            }
            // Вкладка "Все"
            FolderTabs.Children.Add(MakeFolderTab(Loc.T("folder_all"), -1));
            foreach (var f in folders) {
                int fid = f["id"]?.ToObject<int>() ?? 0;
                var titleToken = f["name"];
                // chatFolderInfo.name = chatFolderName { text: formattedText { text: string } }
                string fname = titleToken?["text"]?["text"]?.ToString()  // chatFolderName.text.text
                            ?? titleToken?["text"]?.ToString()            // chatFolderName.text если строка
                            ?? titleToken?.ToString()                     // fallback
                            ?? Loc.T("label_folder");
                FolderTabs.Children.Add(MakeFolderTab(fname, fid));
                // Запрашиваем чаты папки по одной за раз через очередь
                _folderLoadQueue.Enqueue(fid);
            }
            FolderTabsScroll.Visibility = Visibility.Visible;
            UpdateFolderTabStyles();
            // Запускаем загрузку папок только если основной список уже загружен
            if (_mainListLoaded) LoadNextFolder();
        }

        private Button MakeFolderTab(string title, int folderId) {
            var btn = new Button {
                Content = title,
                Tag = folderId,
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.White),
                FontSize = 14,
                Padding = new Thickness(12, 8, 12, 8),
                BorderThickness = new Thickness(0)
            };
            btn.Click += (s, e) => SwitchFolder((int)(btn.Tag));
            return btn;
        }

        private void SwitchFolder(int folderId) {
            _currentFolderId = folderId;
            UpdateFolderTabStyles();
            if (ArchiveRow != null)
                ArchiveRow.Visibility = folderId == -1 ? Visibility.Visible : Visibility.Collapsed;
            if (folderId == -1) {
                _chatListItems.Clear();
                foreach (var c in _allChatItems)
                    _chatListItems.Add(c);
            } else {
                _chatListItems.Clear();
                if (_folderChatIds.ContainsKey(folderId)) {
                    foreach (var id in _folderChatIds[folderId]) {
                        if (_chatsDict.ContainsKey(id))
                            _chatListItems.Add(_chatsDict[id]);
                    }
                }
            }
            ChatCountText.Text = _chatListItems.Count.ToString();
        }

        private void UpdateFolderTabStyles() {
            bool light = _isLightTheme;
            var inactiveColor = light
                ? Windows.UI.Color.FromArgb(255, 100, 100, 100)  // тёмно-серый для светлой
                : Windows.UI.Colors.White;
            foreach (var child in FolderTabs.Children) {
                var btn = child as Button;
                if (btn == null) continue;
                bool isActive = (int)(btn.Tag) == _currentFolderId;
                btn.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                    isActive ? Windows.UI.Color.FromArgb(255, 42, 171, 238) : inactiveColor);
                btn.BorderBrush = new Windows.UI.Xaml.Media.SolidColorBrush(
                    isActive ? Windows.UI.Color.FromArgb(255, 42, 171, 238) : Windows.UI.Colors.Transparent);
                btn.BorderThickness = new Thickness(0, 0, 0, isActive ? 2 : 0);
                if (light)
                    btn.Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            }
        }

        private void UpdateAppBadge(int count) {
            try {
                var badgeXml = Windows.UI.Notifications.BadgeUpdateManager.GetTemplateContent(
                    Windows.UI.Notifications.BadgeTemplateType.BadgeNumber);
                var badgeNode = badgeXml.SelectSingleNode("/badge");
                if (badgeNode?.Attributes != null) {
                    var attr = badgeXml.CreateAttribute("value");
                    attr.NodeValue = count > 0 ? count.ToString() : "0";
                    badgeNode.Attributes.SetNamedItem(attr);
                }
                var badge = new Windows.UI.Notifications.BadgeNotification(badgeXml);
                Windows.UI.Notifications.BadgeUpdateManager.CreateBadgeUpdaterForApplication().Update(badge);
            } catch { }
        }

        private void NotifyAllChatTheme() {
            foreach (var c in _chatsDict.Values) c.NotifyThemeChanged();
            foreach (var r in _searchAllResults) r.NotifyTitleColor();
            foreach (var r in _chatSearchResultItems) r.NotifyTitleColor();
        }

        private void UpdateBubbleColors() {
            // Перекрашиваем уже загруженные сообщения
            foreach (var m in _messageItems)
                if (!m.IsSeparator)
                    m.Background = m.IsOutgoing ? BubbleColorOut : BubbleColorIn;
        }

        // ======= СТИКЕРЫ =======

        // ======= КОНТАКТЫ =======

        private async Task HandleContactsLoaded(JArray userIds) {
            var contacts = new List<ContactItem>();
            foreach (var uid2 in userIds) {
                long cid2 = uid2.ToObject<long>();
                if (_usersDict.ContainsKey(cid2)) {
                    var u2 = _usersDict[cid2];
                    contacts.Add(new ContactItem {
                        UserId   = cid2,
                        FullName = cid2 == _myUserId ? Loc.T("menu_favorites") : (u2["first_name"]?.ToString() + " " + u2["last_name"]?.ToString()).Trim(),
                        Username = cid2 == _myUserId ? "" : (u2["username"]?.ToString() ?? u2["usernames"]?["editable_username"]?.ToString() ?? ""),
                        LastSeen = cid2 == _myUserId ? "" : GetLastSeenText(u2["status"])
                    });
                } else {
                    // Нет данных — добавляем заглушку и запрашиваем
                    contacts.Add(new ContactItem { UserId = cid2, FullName = "..." });
                    TdJson.SendUtf8(_client, "{\"@type\":\"getUser\",\"user_id\":" + cid2 + "}");
                }
            }
            // Убираем себя из обычного списка — добавим как "Избранное" первым
            foreach (var cx in contacts)
            contacts = contacts.Where(c => c.UserId != _myUserId).OrderBy(c => c.FullName).ToList();
            if (_myUserId != 0) {
                var selfItem = new ContactItem { UserId = _myUserId, FullName = Loc.T("menu_favorites") };
                contacts.Insert(0, selfItem);
                if (_usersDict.ContainsKey(_myUserId)) {
                    var t = LoadContactAvatarFromUser(selfItem, _usersDict[_myUserId]);
                }
            } else {
            }
            _contactItems = contacts;
            if (_myUserId == 0) _contactsPendingMyId = true;
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                ContactsLoadingText.Visibility = Visibility.Collapsed;
                ContactsListView.ItemsSource   = _contactItems;
                foreach (var contact in _contactItems)
                    if (_usersDict.ContainsKey(contact.UserId))
                        { var t = LoadContactAvatarFromUser(contact, _usersDict[contact.UserId]); }
            });
        }

        private string GetLastSeenText(JToken status) {
            if (status == null) return "";
            string stype = status["@type"]?.ToString() ?? "";
            switch (stype) {
                case "userStatusOnline": return Loc.T("hdr_online");
                case "userStatusOffline":
                    int wasOnline = status["was_online"]?.ToObject<int>() ?? 0;
                    if (wasOnline == 0) return Loc.T("ls_longAgo");
                    var dt = DateTimeOffset.FromUnixTimeSeconds(wasOnline).LocalDateTime;
                    var now = DateTime.Now;
                    if (dt.Date == now.Date) return Loc.T("ls_todayAt") + dt.ToString("HH:mm");
                    if (dt.Date == now.Date.AddDays(-1)) return Loc.T("ls_yesterdayAt") + dt.ToString("HH:mm");
                    if ((now - dt).TotalDays < 7) return Loc.T("hdr_wasSeenPrefix") + dt.ToString("dddd", LocCulture()) + ", " + dt.ToString("HH:mm");
                    return Loc.T("hdr_wasSeenPrefix") + dt.ToString("dd.MM.yyyy");
                case "userStatusRecently": return Loc.T("hdr_recently");
                case "userStatusLastWeek": return Loc.T("hdr_lastWeek");
                case "userStatusLastMonth": return Loc.T("hdr_lastMonth");
                default: return "";
            }
        }

        private async Task LoadContactAvatarFromUser(ContactItem contact, JToken user) {
            var ph = user["profile_photo"]?["small"] as JObject;
            if (ph == null) return;
            long pfid = ph["id"]?.ToObject<long>() ?? 0;
            string pPath = ph["local"]?["path"]?.ToString();
            if (!string.IsNullOrEmpty(pPath))
                await LoadContactAvatar(contact, pPath);
            else if (pfid > 0)
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + pfid + ",\"priority\":1,\"synchronous\":false}");
        }

        private const string ToastGroup = "unogram";

        private static string ToastTagForChat(long chatId) {
            // Tag ограничен по длине, поэтому только цифры без знака.
            return "c" + (chatId < 0 ? "n" : "") + System.Math.Abs(chatId).ToString();
        }

        /// <summary>Убирает из центра уведомлений всё, что относится к чату.</summary>
        private static void RemoveToastsForChat(long chatId) {
            try {
                Windows.UI.Notifications.ToastNotificationManager.History
                    .Remove(ToastTagForChat(chatId), ToastGroup);
            } catch { }
        }

        /// <summary>Копирует уже скачанный локальный файл в общую библиотеку (Фото/Видео/Музыка).</summary>
        /// <summary>
        /// KnownFolders (MusicLibrary/PicturesLibrary/VideosLibrary) оказались
        /// ненадёжны на этой сборке Windows 10 Mobile — Path у них резолвился
        /// в пустую строку, отсюда и Access is denied при прямом копировании.
        /// FileSavePicker работает через системный брокер и не требует этих
        /// capability вообще — минус в том, что теперь при каждом сохранении
        /// показывается системный диалог "куда сохранить", а не тихо в один тап.
        /// </summary>
        private async Task<bool> SaveFileToLibraryAsync(string sourcePath, Windows.Storage.Pickers.PickerLocationId suggestedLocation) {
            try {
                if (string.IsNullOrEmpty(sourcePath)) return false;
                var srcFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                string ext = System.IO.Path.GetExtension(srcFile.Name);
                if (string.IsNullOrEmpty(ext)) ext = ".dat";

                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = suggestedLocation;
                picker.SuggestedFileName = srcFile.Name;
                picker.FileTypeChoices.Add(ext.TrimStart('.').ToUpperInvariant() + " (" + ext + ")",
                    new List<string> { ext });

                var destFile = await picker.PickSaveFileAsync();
                if (destFile == null) return false; // пользователь сам отменил — не ошибка, просто без тоста об успехе

                using (var srcStream = await srcFile.OpenReadAsync())
                using (var destStream = await destFile.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite)) {
                    await Windows.Storage.Streams.RandomAccessStream.CopyAsync(srcStream, destStream);
                    await destStream.FlushAsync();
                }
                try { await Windows.Storage.CachedFileManager.CompleteUpdatesAsync(destFile); } catch { }

                return true;
            } catch (Exception ex) {
                Log("SAVE ERR: " + ex.Message);
                return false;
            }
        }

        /// <summary>Сохраняет файл (через FileSavePicker) и показывает лёгкий тост с результатом (fire-and-forget).</summary>
        private async void SaveAndToast(string sourcePath, Windows.Storage.Pickers.PickerLocationId suggestedLocation) {
            bool ok = await SaveFileToLibraryAsync(sourcePath, suggestedLocation);
            ShowToastNotification(Loc.T(ok ? "toast_saved" : "toast_save_failed"), "", 0);
        }

        /// <summary>
        /// <paramref name="silentWhenOnScreen"/> marks a message notification:
        /// with the app on screen the user does not need to be alerted by
        /// sound, but the banner still has to appear — it is what tells them an
        /// event happened and what puts the glyph in the notification bar.
        /// Toasts that are direct feedback for an action the user just took
        /// (e.g. "saved") pass false and keep their sound.
        /// </summary>
        private void ShowToastNotification(string title, string body, long chatId,
                                           bool silentWhenOnScreen = false) {
            try {
                bool silent = silentWhenOnScreen && BackgroundService.IsAppOnScreen;
                // Строим XML вручную — полный контроль над звуком
                string audio = silent
                    ? @"<audio silent=""true""/>"
                    : @"<audio src=""ms-winsoundevent:Notification.IM"" loop=""false""/>";
                string xml = $@"<toast duration=""short"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>{EscapeXml(title)}</text>
      <text>{EscapeXml(body)}</text>
    </binding>
  </visual>
  {audio}
</toast>";
                var toastXml = new Windows.Data.Xml.Dom.XmlDocument();
                toastXml.LoadXml(xml);
                var toast = new Windows.UI.Notifications.ToastNotification(toastXml);
                // SuppressPopup is deliberately not used: it does deliver the
                // notification to the centre, but without the banner the shell
                // raises no status-bar glyph, so the user never learns anything
                // arrived. Silencing the audio alone keeps the notice visible.
                // Tag/Group нужны, чтобы уведомление можно было потом убрать из
                // центра уведомлений — без них History.Remove адресовать нечего.
                toast.Tag = ToastTagForChat(chatId);
                toast.Group = ToastGroup;
                // Без ExpirationTime — уведомление живёт стандартное время
                // Показываем из любого потока через Dispatcher
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    try {
                        Windows.UI.Notifications.ToastNotificationManager
                            .CreateToastNotifier().Show(toast);
                    } catch (Exception ex2) { Log("Toast show ERR: " + ex2.Message); }
                });
            } catch (Exception ex) { Log("Toast ERR: " + ex.Message); }
        }

        private static string EscapeXml(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // ================================================================
        // Живая плитка (Live Tile) — превью последних непрочитанных сообщений.
        // Данные хранятся в LocalSettings под общим ключом, потому что их
        // пишет и читает не только этот процесс, но и CatchUpTask —
        // полностью отдельный процесс фоновой задачи со своим кодом
        // (см. UnogramBackground/CatchUpTask.cs), с которым нельзя
        // напрямую разделить in-memory состояние.
        // ================================================================
        private const string TilePreviewsKey = "tile_previews";
        private const int MaxTilePreviews = 5;

        /// <summary>Добавляет новое сообщение в превью плитки и сразу применяет её.</summary>
        private void AddTilePreviewAndUpdate(long chatId, string sender, string text) {
            try {
                var list = LoadTilePreviews();
                list.Insert(0, new JObject { ["c"] = chatId, ["s"] = sender ?? "", ["t"] = text ?? "" });
                if (list.Count > MaxTilePreviews) list.RemoveRange(MaxTilePreviews, list.Count - MaxTilePreviews);
                SaveTilePreviews(list);
                ApplyTileXml(list);
            } catch { }
        }

        /// <summary>Убирает из превью все записи конкретного чата (открыли/прочитали) и обновляет плитку.</summary>
        private void ClearTilePreviewsForChat(long chatId) {
            try {
                var list = LoadTilePreviews();
                int removed = list.RemoveAll(o => (o["c"]?.ToObject<long>() ?? 0) == chatId);
                if (removed > 0) { SaveTilePreviews(list); ApplyTileXml(list); }
            } catch { }
        }

        /// <summary>Полностью очищает плитку (например, когда непрочитанных не осталось вовсе).</summary>
        private static void ClearLiveTile() {
            try {
                Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove(TilePreviewsKey);
                Windows.UI.Notifications.TileUpdateManager.CreateTileUpdaterForApplication().Clear();
            } catch { }
        }

        private static List<JObject> LoadTilePreviews() {
            try {
                var v = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                if (!v.ContainsKey(TilePreviewsKey)) return new List<JObject>();
                var arr = JArray.Parse((string)v[TilePreviewsKey]);
                var result = new List<JObject>();
                foreach (var t in arr) if (t is JObject jo) result.Add(jo);
                return result;
            } catch { return new List<JObject>(); }
        }

        private static void SaveTilePreviews(List<JObject> list) {
            try {
                var arr = new JArray();
                foreach (var o in list) arr.Add(o);
                Windows.Storage.ApplicationData.Current.LocalSettings.Values[TilePreviewsKey] =
                    arr.ToString(Newtonsoft.Json.Formatting.None);
            } catch { }
        }

        private static void ApplyTileXml(List<JObject> list) {
            try {
                if (list.Count == 0) {
                    Windows.UI.Notifications.TileUpdateManager.CreateTileUpdaterForApplication().Clear();
                    return;
                }
                var tileXml = new Windows.Data.Xml.Dom.XmlDocument();
                tileXml.LoadXml(BuildTileXml(list));
                var tileNotif = new Windows.UI.Notifications.TileNotification(tileXml);
                Windows.UI.Notifications.TileUpdateManager.CreateTileUpdaterForApplication().Update(tileNotif);
            } catch { }
        }

        /// <summary>
        /// Собирает XML для трёх размеров плитки сразу (Medium/Wide/Large) —
        /// Windows сама покажет тот вариант, который подходит текущему
        /// размеру закрепления, без дополнительной логики с нашей стороны.
        /// </summary>
        private static string BuildTileXml(List<JObject> list) {
            string Line(JObject p, int senderLen, int textLen) =>
                EscapeXml(TrimForTile(p["s"]?.ToString(), senderLen) + ": " + TrimForTile(p["t"]?.ToString(), textLen));

            var sb = new System.Text.StringBuilder();
            sb.Append("<tile><visual version=\"2\">");

            // Small — физически места хватает почти только на иконку, поэтому
            // тут одна короткая строка (имя отправителя последнего сообщения),
            // а не полноценный превью-текст, как в остальных размерах. Но
            // важно, чтобы этот binding вообще присутствовал — без него
            // плитка, закреплённая в размере Small, никогда не покажет ничего
            // "живого", сколько бы сообщений ни пришло.
            sb.Append("<binding template=\"TileSmall\" branding=\"none\">");
            sb.Append("<text hint-style=\"base\" hint-align=\"center\">").Append(EscapeXml(TrimForTile(list[0]["s"]?.ToString(), 12))).Append("</text>");
            sb.Append("</binding>");

            sb.Append("<binding template=\"TileMedium\" branding=\"nameAndLogo\">");
            foreach (var p in list.GetRange(0, Math.Min(2, list.Count))) {
                sb.Append("<text hint-style=\"captionSubtle\">").Append(EscapeXml(TrimForTile(p["s"]?.ToString(), 20))).Append("</text>");
                sb.Append("<text hint-style=\"caption\">").Append(EscapeXml(TrimForTile(p["t"]?.ToString(), 40))).Append("</text>");
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
            return sb.ToString();
        }

        private static string TrimForTile(string s, int maxLen) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > maxLen ? s.Substring(0, maxLen) + "…" : s;
        }




        private void Favorites_Click(object sender, RoutedEventArgs e) {
            // Открываем чат с самим собой (Избранное)
            if (_myUserId == 0) return;
            // Ищем чат с собой — его ID совпадает с _myUserId
            if (_chatsDict.ContainsKey(_myUserId))
                OpenChat(_chatsDict[_myUserId], 0);
            else
                TdJson.SendUtf8(_client, "{\"@type\":\"createPrivateChat\",\"user_id\":" + _myUserId + ",\"force\":true}");
        }

        /// <summary>
        /// Постоянная работа в фоне. По умолчанию выключено: режим требует
        /// доступа к геопозиции и заметно расходует батарею.
        /// </summary>
        private void Language_Click(object sender, RoutedEventArgs e) {
            var item = sender as MenuFlyoutItem;
            string code = item == null ? null : item.Tag as string;
            if (string.IsNullOrEmpty(code) || code == Loc.Language) return;
            Loc.Language = code;
            ApplyLanguage();
        }

        /// <summary>
        /// Re-labels everything that has a localized string. Only the settings
        /// menu is covered so far — the rest of MainPage.xaml still holds
        /// hard-coded Russian and needs the same treatment.
        /// </summary>
        private void ApplyLanguage() {
            try {
                FavoritesItem.Text   = Loc.T("menu_favorites");
                ClearCacheItem.Text  = Loc.T("menu_clearCache");
                SoundToggleItem.Text = _soundEnabled ? Loc.T("menu_sound_on") : Loc.T("menu_sound_off");
                LanguageSubItem.Text = Loc.T("menu_language");
                LogoutItem.Text      = Loc.T("menu_logout");

                // Вкладка "Все" собирается динамически (BuildFolderTabs) и не
                // пересоздаётся при смене языка — обновляем её текст точечно,
                // остальные вкладки — это пользовательские названия папок,
                // их трогать не нужно.
                foreach (var child in FolderTabs.Children) {
                    var tabButton = child as Button;
                    if (tabButton != null && tabButton.Tag is int && (int)tabButton.Tag == -1) {
                        tabButton.Content = Loc.T("folder_all");
                        break;
                    }
                }
                UpdateKeepAliveMenuText();
                UpdateCatchUpMenuText();

                // Hebrew is right-to-left; flipping the root mirrors the whole tree.
                var root = Window.Current.Content as FrameworkElement;
                if (root != null)
                    root.FlowDirection = Loc.IsRightToLeft(Loc.Language)
                        ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            } catch { }
        }

        private async void KeepAliveToggle_Click(object sender, RoutedEventArgs e) {
            if (BackgroundService.Instance.KeepAliveActive) {
                BackgroundService.KeepAliveEnabled = false;
                BackgroundService.Instance.StopKeepAlive();
                UpdateKeepAliveMenuText();
                return;
            }

            var dialog = new Windows.UI.Popups.MessageDialog(
                Loc.T("keepAlive_body"), Loc.T("keepAlive_title"));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_enable"), async cmd => {
                BackgroundService.KeepAliveEnabled = true;
                bool ok = await BackgroundService.Instance.StartKeepAliveAsync();
                UpdateKeepAliveMenuText();
                if (!ok) {
                    BackgroundService.KeepAliveEnabled = false;
                    UpdateKeepAliveMenuText();
                    await new Windows.UI.Popups.MessageDialog(
                        Loc.T("keepAlive_failed"), Loc.T("keepAlive_title")).ShowAsync();
                }
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_cancel")));
            await dialog.ShowAsync();
        }

        private void UpdateKeepAliveMenuText() {
            try {
                KeepAliveToggleItem.Text = BackgroundService.Instance.KeepAliveActive
                    ? Loc.T("menu_keepAlive_on") : Loc.T("menu_keepAlive_off");
            } catch { }
        }

        private void SoundToggle_Click(object sender, RoutedEventArgs e) {
            _soundEnabled = !_soundEnabled;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["sound_enabled"] = _soundEnabled;
            SoundToggleItem.Text = _soundEnabled ? Loc.T("menu_sound_on") : Loc.T("menu_sound_off");
        }

        /// <summary>
        /// Включает/выключает CatchUpTask — фоновую доставку уведомлений при
        /// полностью закрытом приложении. В отличие от "Background mode"
        /// (KeepAlive, продлевает жизнь уже открытой сессии), это отдельная
        /// периодическая задача (TimeTrigger), которая иначе всегда
        /// регистрируется без спроса. Применяем изменение сразу, не дожидаясь
        /// следующего запуска.
        /// </summary>
        private async void CatchUpToggle_Click(object sender, RoutedEventArgs e) {
            BackgroundService.CatchUpEnabled = !BackgroundService.CatchUpEnabled;
            if (BackgroundService.CatchUpEnabled)
                await BackgroundService.RegisterCatchUpTaskAsync();
            else
                BackgroundService.UnregisterCatchUpTask();
            UpdateCatchUpMenuText();
        }

        private void UpdateCatchUpMenuText() {
            try {
                CatchUpToggleItem.Text = BackgroundService.CatchUpEnabled
                    ? Loc.T("menu_catchup_on") : Loc.T("menu_catchup_off");
            } catch { }
        }

        private async void ClearCache_Click(object sender, RoutedEventArgs e) {
            var dialog = new Windows.UI.Popups.MessageDialog(
                Loc.T("dlg_clearCache_body"), Loc.T("dlg_clearCache_title"));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_clear"), async cmd => {
                try {
                    // TDLib API — очищаем кэш файлов
                    _optimizeStorageTcs = new TaskCompletionSource<bool>();
                    TdJson.SendUtf8(_client, "{\"@type\":\"optimizeStorage\",\"size\":0,\"ttl\":0,\"count\":0,\"immunity_delay\":0" +
                        ",\"file_types\":[{\"@type\":\"fileTypePhoto\"},{\"@type\":\"fileTypeVideo\"},{\"@type\":\"fileTypeAudio\"}" +
                        ",{\"@type\":\"fileTypeAnimation\"},{\"@type\":\"fileTypeDocument\"}]" +
                        ",\"chat_ids\":[],\"exclude_chat_ids\":[],\"return_deleted_file_statistics\":true,\"chat_limit\":0}");
                    // Ждём реального подтверждения (storageStatistics), а не просто
                    // сам факт отправки команды — optimizeStorage не мгновенна, и
                    // если открыть чат раньше, чем TDLib реально дочистит файлы и
                    // обновит свою БД, часть аудио/видео будет считаться "уже
                    // скачанными" по старому пути и не перекачается. На случай,
                    // если ответ вдруг не придёт — подстраховываемся таймаутом,
                    // чтобы диалог не завис навсегда.
                    var completed = await Task.WhenAny(_optimizeStorageTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
                    _optimizeStorageTcs = null;

                    var confirmDialog = new Windows.UI.Popups.MessageDialog(Loc.T("dlg_cacheCleared_body"), Loc.T("dlg_done_title"));
                    await confirmDialog.ShowAsync();
                } catch { }
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_cancel")));
            await dialog.ShowAsync();
        }

        private ObservableCollection<ChatItem> _searchResults = new ObservableCollection<ChatItem>();
        private ObservableCollection<SearchResultItem> _searchAllResults = new ObservableCollection<SearchResultItem>();
        private ObservableCollection<SearchMessageItem> _searchMessageResults = new ObservableCollection<SearchMessageItem>();
        private string _searchQuery = "";

        private int _searchToken = 0;
        private void SearchBox_TextChanged(object sender, Windows.UI.Xaml.Controls.TextChangedEventArgs e) {
            _searchQuery = SearchBox.Text ?? "";
            SearchClearButton.Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Collapsed : Visibility.Visible;
            if (string.IsNullOrEmpty(_searchQuery)) {
                SearchResultsView.Visibility = Visibility.Collapsed;
                ChatListView.Visibility = Visibility.Visible;
                if (FolderTabsScroll != null) FolderTabsScroll.Visibility = _folderChatIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            } else {
                ChatListView.Visibility = Visibility.Collapsed;
                if (FolderTabsScroll != null) FolderTabsScroll.Visibility = Visibility.Collapsed;
                SearchResultsView.Visibility = Visibility.Visible;
                _searchAllResults.Clear();
                if (SearchResultsView.ItemsSource == null) SearchResultsView.ItemsSource = _searchAllResults;
                _searchToken++;
                int myToken = _searchToken;
                // Локальный поиск по чатам
                string q = _searchQuery.ToLower();
                bool anyChats = false;
                foreach (var c in _allChatItems) {
                    if (c.Title?.ToLower().Contains(q) == true) {
                        if (!anyChats) {
                            _searchAllResults.Add(new SearchResultItem { Type = SearchResultItem.ResultType.Header, Title = Loc.T("search_chats") });
                            anyChats = true;
                        }
                        _searchAllResults.Add(new SearchResultItem {
                            Type = SearchResultItem.ResultType.Chat,
                            ChatId = c.Id, Title = c.Title,
                            Subtitle = c.LastMessage, Photo = c.Photo
                        });
                    }
                }
                // TDLib поиск
                TdJson.SendUtf8(_client, "{\"@type\":\"searchChats\",\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\",\"limit\":50}");
                TdJson.SendUtf8(_client, "{\"@type\":\"searchChatsOnServer\",\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\",\"limit\":50}");
                TdJson.SendUtf8(_client, "{\"@type\":\"searchPublicChats\",\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\"}");
                TdJson.SendUtf8(_client, "{\"@type\":\"searchMessages\",\"chat_list\":{\"@type\":\"chatListMain\"},\"query\":\"" + _searchQuery.Replace("\"","\\\"") + "\",\"limit\":20,\"offset\":\"\"}");
            }
        }

        private void SearchClear_Click(object sender, RoutedEventArgs e) {
            SearchBox.Text = "";
            _searchQuery = "";
            SearchClearButton.Visibility = Visibility.Collapsed;
            SearchResultsView.Visibility = Visibility.Collapsed;
            ChatListView.Visibility = Visibility.Visible;
            if (FolderTabsScroll != null) FolderTabsScroll.Visibility = _folderChatIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchResult_ItemClick(object sender, ItemClickEventArgs e) {
            var item = e.ClickedItem as SearchResultItem;
            if (item == null || item.IsHeader) return;
            SearchBox.Text = "";
            _searchQuery = "";
            SearchClearButton.Visibility = Visibility.Collapsed;
            SearchResultsView.Visibility = Visibility.Collapsed;
            ChatListView.Visibility = Visibility.Visible;
            if (FolderTabsScroll != null) FolderTabsScroll.Visibility = _folderChatIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (item.Type == SearchResultItem.ResultType.Message)
                _pendingScrollToMsgId = item.MessageId;
            if (_chatsDict.ContainsKey(item.ChatId))
                OpenChat(_chatsDict[item.ChatId], 0);
        }

        private void SearchMessage_ItemClick(object sender, ItemClickEventArgs e) { }

        private void ApplySearch() { }

        private void ContactsButton_Click(object sender, RoutedEventArgs e) {
            ContactsOverlay.Visibility = Visibility.Visible;
            ContactsListView.ItemsSource = null;
            ContactsLoadingText.Visibility = Visibility.Visible;
            if (_myUserId == 0) {
                _waitingForMe = true;
                TdJson.SendUtf8(_client, "{\"@type\":\"getMe\"}");
            }
            TdJson.SendUtf8(_client, "{\"@type\":\"getContacts\"}");
        }

        private void ContactsOverlay_Close(object sender, RoutedEventArgs e) {
            ContactsOverlay.Visibility = Visibility.Collapsed;
        }

        private void ContactItem_Click(object sender, Windows.UI.Xaml.Controls.ItemClickEventArgs e) {
            var contact = e.ClickedItem as ContactItem;
            if (contact == null) return;
            ContactsOverlay.Visibility = Visibility.Collapsed;
            // createPrivateChat вернёт существующий чат или создаст новый
            _pendingContactUserId = contact.UserId;
            TdJson.SendUtf8(_client, "{\"@type\":\"createPrivateChat\",\"user_id\":" + contact.UserId + ",\"force\":true}");
        }

        private async Task LoadContactAvatar(ContactItem contact, string path) {
            try {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                var bmp = new BitmapImage();
                bmp.DecodePixelWidth = 100;
                using (var stream = await file.OpenReadAsync())
                    await bmp.SetSourceAsync(stream);
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    contact.Photo = bmp;
                    contact.OnPropertyChanged("Photo");
                    contact.OnPropertyChanged("NoPhotoVisibility");
                });
            } catch { }
        }

        private void StickerButton_Click(object sender, RoutedEventArgs e) {
            if (_stickerPanelOpen) {
                StickerPanel.Visibility = Visibility.Collapsed;
                _stickerPanelOpen = false;
                return;
            }
            StickerPanel.Visibility = Visibility.Visible;
            _stickerPanelOpen = true;
            if (_loadedStickerSetIds.Count == 0) {
                StickerGrid.ItemsSource = null;
                StickerLoadingText.Text = Loc.T("status_loading");
                StickerProgressText.Text = "";
                StickerLoadingPanel.Visibility = Visibility.Visible;
                StickerPackTabs.Children.Clear();
                TdJson.SendUtf8(_client, "{\"@type\":\"getInstalledStickerSets\",\"sticker_type\":{\"@type\":\"stickerTypeRegular\"}}");
            }
        }

        private void StickerGrid_ItemClick(object sender, Windows.UI.Xaml.Controls.ItemClickEventArgs e) {
            var item = e.ClickedItem as StickerItem;
            if (item == null) return;
            StickerPanel.Visibility = Visibility.Collapsed;
            _stickerPanelOpen = false;

            if (!string.IsNullOrEmpty(item.RemoteFileId)) {
                string sReq = "{\"@type\":\"sendMessage\",\"chat_id\":" + _currentChatId +
                    (_threadMessageId != 0 ? ",\"topic_id\":{\"@type\":\"messageTopicThread\",\"message_thread_id\":" + _threadMessageId + "}" +
                                             ",\"message_thread_id\":" + _threadMessageId : "") +
                    ",\"input_message_content\":{\"@type\":\"inputMessageSticker\"" +
                    ",\"sticker\":{\"@type\":\"inputSticker\"" +
                    ",\"sticker\":{\"@type\":\"inputFileRemote\",\"id\":\"" + item.RemoteFileId + "\"}" +
                    ",\"width\":512,\"height\":512}}}";
                TdJson.SendUtf8(_client, sReq);
            } else {
                // Нет remote id — скачиваем и отправляем по file_id
                _pendingStickerFileId = item.FileId;
                _pendingStickerChatId = _currentChatId;
                TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + item.FileId + ",\"priority\":32,\"synchronous\":false}");
            }
        }

        private void LoadStickerSet(long setId) {
            if (_loadedStickerSetIds.Contains(setId)) return;
            _loadedStickerSetIds.Add(setId);
            TdJson.SendUtf8(_client, "{\"@type\":\"getStickerSet\",\"set_id\":" + setId + "}");
        }

        private void HandleStickerSets(Newtonsoft.Json.Linq.JToken update) {
            var sets = update["sets"] as Newtonsoft.Json.Linq.JArray;
            if (sets == null) return;
            // Добавляем вкладки и загружаем первый пак
            StickerPackTabs.Children.Clear();
            bool first = true;
            foreach (var s in sets) {
                long sid = s["id"]?.ToObject<long>() ?? 0;
                if (sid == 0) continue;
                string name = s["title"]?.ToString() ?? "?";
                var btn = new Windows.UI.Xaml.Controls.Button {
                    Content = name.Length > 6 ? name.Substring(0, 6) : name,
                    FontSize = 11,
                    Padding = new Windows.UI.Xaml.Thickness(8, 4, 8, 4),
                    Background = first ? CB("#0088cc") : CB("#333333"),
                    Foreground = CB("#FFFFFF"),
                    Tag = sid
                };
                long capturedSid = sid;
                btn.Click += (s2, e2) => {
                    foreach (var child in StickerPackTabs.Children)
                        if (child is Windows.UI.Xaml.Controls.Button b)
                            b.Background = CB("#333333");
                    ((Windows.UI.Xaml.Controls.Button)s2).Background = CB("#0088cc");
                    ShowStickerSet(capturedSid);
                };
                StickerPackTabs.Children.Add(btn);
                if (first) {
                    _currentStickerSetId = sid; // первый пак — устанавливаем сразу
                    LoadStickerSet(sid);
                    first = false;
                }
            }
        }

        private long _currentStickerSetId = 0;

        private void ShowStickerSet(long setId) {
            _currentStickerSetId = setId;
            var existing = _currentStickerItems.Where(s => s.SetId == setId).ToList();
            if (existing.Count > 0) {
                StickerGrid.ItemsSource = existing;
                StickerLoadingPanel.Visibility = Visibility.Collapsed;
                UpdateStickerProgress(setId);
            } else {
                StickerGrid.ItemsSource = null;
                StickerLoadingText.Text = Loc.T("status_loading");
                StickerProgressText.Text = "";
                StickerLoadingPanel.Visibility = Visibility.Visible;
                LoadStickerSet(setId);
            }
        }

        private async void HandleStickerSet(Newtonsoft.Json.Linq.JToken update) {
            long setId = update["id"]?.ToObject<long>() ?? 0;
            var stickers = update["stickers"] as Newtonsoft.Json.Linq.JArray;
            if (stickers == null || setId == 0) return;
            int total = stickers.Count;

            var items = new List<StickerItem>();
            int downloadCount = 0;

            foreach (var st in stickers) {
                var stickerFile = st["sticker"] as JObject;
                if (stickerFile == null) continue;
                long fid = stickerFile["id"]?.ToObject<long>() ?? 0;
                string remoteId = stickerFile["remote"]?["id"]?.ToString() ?? "";
                var item = new StickerItem { SetId = setId, FileId = fid, RemoteFileId = remoteId };
                items.Add(item);

                var thumb = st["thumbnail"];
                var thumbFile = thumb?["file"] as JObject;
                if (thumbFile != null) {
                    long tfid = thumbFile["id"]?.ToObject<long>() ?? 0;
                    item.ThumbFileId = tfid;
                    string tPath = thumbFile["local"]?["path"]?.ToString();
                    if (!string.IsNullOrEmpty(tPath) &&
                        (tPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                         tPath.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase))) {
                        var capturedItem = item;
                        var capturedSetId = setId;
                        _ = LoadStickerThumbAsync(tPath).ContinueWith(t2 => {
                            if (t2.Result != null) {
                                capturedItem.Thumb = t2.Result;
                                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => {
                                    UpdateStickerProgress(capturedSetId);
                                });
                            }
                        }, TaskScheduler.Default);
                    } else if (tfid > 0) {
                        _stickerThumbToItem[tfid] = fid;
                        if (downloadCount < 20) {
                            TdJson.SendUtf8(_client, "{\"@type\":\"downloadFile\",\"file_id\":" + tfid + ",\"priority\":3,\"synchronous\":false}");
                            downloadCount++;
                        }
                    }
                }
            }

            _currentStickerItems.RemoveAll(s => s.SetId == setId);
            _currentStickerItems.AddRange(items);

            // Показываем сразу все ячейки (с пустыми thumb), скрываем "Загрузка..."
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                if (StickerPanel.Visibility == Visibility.Visible && _currentStickerSetId == setId) {
                    StickerGrid.ItemsSource = _currentStickerItems.Where(s => s.SetId == setId).ToList();
                    StickerLoadingPanel.Visibility = Visibility.Collapsed;
                    UpdateStickerProgress(setId);
                }
            });
        }

        // Обновляем счётчик загруженных thumbnail
        private void UpdateStickerProgress(long setId) {
            if (_currentStickerSetId != setId || StickerPanel.Visibility != Visibility.Visible) return;
            var setItems = _currentStickerItems.Where(s => s.SetId == setId).ToList();
            int loaded = setItems.Count(s => s.Thumb != null);
            int total  = setItems.Count;
            if (total == 0) return;
            if (loaded < total) {
                StickerProgressText.Text = loaded + " / " + total;
                StickerProgressText.Visibility = Visibility.Visible;
            } else {
                StickerProgressText.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<BitmapImage> LoadStickerThumbAsync(string path) {
            try {
                if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    byte[] data;
                    using (var stream = await file.OpenReadAsync())
                    using (var reader = new Windows.Storage.Streams.DataReader(stream)) {
                        await reader.LoadAsync((uint)stream.Size);
                        data = new byte[stream.Size];
                        reader.ReadBytes(data);
                    }
                    var wb = await WebPDecoder.DecodeAsync(data);
                    // Конвертируем WriteableBitmap → BitmapImage через InMemoryRandomAccessStream
                    var bmp = new BitmapImage();
                    using (var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream()) {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, ras);
                        // Читаем пиксели из PixelBuffer через Stream
                        byte[] px = new byte[wb.PixelBuffer.Capacity];
                        using (var pixStream = wb.PixelBuffer.AsStream())
                            await pixStream.ReadAsync(px, 0, px.Length);
                        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                            (uint)wb.PixelWidth, (uint)wb.PixelHeight, 96, 96, px);
                        await encoder.FlushAsync();
                        ras.Seek(0);
                        await bmp.SetSourceAsync(ras);
                    }
                    return bmp;
                } else {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    var bmp = new BitmapImage();
                    bmp.DecodePixelWidth = 128;
                    using (var stream = await file.OpenReadAsync())
                        await bmp.SetSourceAsync(stream);
                    return bmp;
                }
            } catch { return null; }
        }

        private async void HandleStickerThumbDownloaded(long fileId, string path) {
            if (!_stickerThumbToItem.ContainsKey(fileId)) return;
            long stickerFid = _stickerThumbToItem[fileId];
            var bmp = await LoadStickerThumbAsync(path);
            if (bmp == null) return;
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                var item = _currentStickerItems.FirstOrDefault(s3 => s3.FileId == stickerFid);
                if (item != null) {
                    item.Thumb = bmp;
                    UpdateStickerProgress(item.SetId);
                }
            });
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e) {
            var dialog = new Windows.UI.Popups.MessageDialog(Loc.T("dlg_logout_body"), Loc.T("dlg_logout_title"));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_logout"), cmd => {
                TdJson.SendUtf8(_client, "{\"@type\":\"logOut\"}");
            }));
            dialog.Commands.Add(new Windows.UI.Popups.UICommand(Loc.T("btn_cancel")));
            dialog.DefaultCommandIndex = 1;
            await dialog.ShowAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) {
            ProfileOverlay.Visibility = Visibility.Collapsed;
            ChatSearchBar.Visibility = Visibility.Collapsed;
            ChatSearchResultsView.Visibility = Visibility.Collapsed;
            ChatHeader.Visibility = Visibility.Visible;
            _chatSearchQuery = "";
            _chatSearchResultIds.Clear();
            _chatSearchResultItems.Clear();
            _chatSearchResultIndex = -1;
            _chatSearchAwaitingResults = false;
            if (_threadMessageId != 0) {
                long channelChatId = _threadChatId;
                _threadMessageId = 0;
                _threadChatId = 0;
                OpenChatById(channelChatId);
                return;
            }
            if (_currentChatId != 0)
                TdJson.SendUtf8(_client, "{\"@type\":\"closeChat\",\"chat_id\":" + _currentChatId + "}");
            _currentChatId = 0;
            _pendingHistoryChatId = 0;
            LoadingIndicator.Visibility = Visibility.Collapsed;
            MessagesListView.Visibility = Visibility.Visible;
            MessagesPanel.Visibility = Visibility.Collapsed;
            // Подстраховка: пересобираем видимый список чатов заново из уже
            // накопленного _chatsDict/_allChatItems (без обращения к серверу —
            // SwitchFolder только перекладывает уже готовые объекты), а не
            // полагаемся только на то, что каждая отдельная вставка/перестановка
            // по ходу апдейтов TDLib была применена без единого расхождения.
            if (!_inArchive) SwitchFolder(_currentFolderId);
            StartPanel.Visibility = Visibility.Visible;
        }

        private void ArchiveRow_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) {
            OpenArchive();
        }

        private void OpenArchive() {
            _inArchive = true;
            ChatListView.ItemsSource = _archiveChatItems;
            MainListHeader.Visibility = Visibility.Collapsed;
            ArchiveListHeader.Visibility = Visibility.Visible;
            ArchiveRow.Visibility = Visibility.Collapsed;

            if (!_archiveLoaded) {
                _archiveLoaded = true;
                _loadingArchive = true;
                TdJson.SendUtf8(_client, "{\"@type\":\"getChats\",\"chat_list\":{\"@type\":\"chatListArchive\"},\"limit\":200}");
            } else {
                ArchiveChatCountText.Text = Loc.T("archive_count") + _archiveChatItems.Count;
            }
        }

        private void ArchiveBack_Click(object sender, RoutedEventArgs e) {
            _inArchive = false;
            ChatListView.ItemsSource = _chatListItems;
            MainListHeader.Visibility = Visibility.Visible;
            ArchiveListHeader.Visibility = Visibility.Collapsed;
            ArchiveRow.Visibility = Visibility.Visible;
        }

        // ================================================================
        // Back button: closes UI layers, and from the main screen exits the
        // app completely
        // ================================================================

        /// <summary>
        /// While there is somewhere to go back to, the topmost UI layer is
        /// closed. From the main screen (chat list, nothing open) there is
        /// nowhere left, and back means exit: the app unloads completely,
        /// along with the background task and location access.
        ///
        /// The home button never reaches this handler — it still just
        /// minimises the app (App.OnSuspending), as before.
        /// </summary>
        private void HandleSystemBackRequest(Windows.UI.Core.BackRequestedEventArgs e) {
            if (_shuttingDown) { e.Handled = true; return; }

            if (PhotoOverlay.Visibility == Visibility.Visible) {
                PhotoOverlay.Visibility = Visibility.Collapsed;
                PhotoOverlayImage.Source = null;
                _fullPhotoMsgId = 0;
            } else if (ProxyPopup != null && ProxyPopup.IsOpen) {
                // Layers reachable straight from the main screen: without these
                // two checks, back from the proxy settings or the contact list
                // would close the app.
                ProxyPopup.IsOpen = false;
            } else if (ContactsOverlay.Visibility == Visibility.Visible) {
                ContactsOverlay_Close(null, null);
            } else if (_currentChatId != 0) {
                BackButton_Click(null, null);
            } else if (_inArchive) {
                ArchiveBack_Click(null, null);
            } else if (!string.IsNullOrEmpty(_searchQuery)) {
                SearchClear_Click(null, null);
            } else {
                var ignored = ExitApplicationAsync();
            }

            // Always mark it handled: an unhandled back is what the system
            // reads as "minimise the app", and the exit is ours to drive now.
            e.Handled = true;
        }

        /// <summary>
        /// Unloads the app completely. The order matters: first drop everything
        /// able to outlive the window (execution extensions, the geolocator,
        /// the background task, audio playback), then close the TDLib session
        /// properly — otherwise the database is left with an open journal —
        /// and only then kill the process.
        /// </summary>
        private async Task ExitApplicationAsync() {
            if (_shuttingDown) return;
            _shuttingDown = true;

            // 1. Background: keep-alive together with the position
            //    subscription, the grace window and the catch-up task.
            try { BackgroundService.Instance.ShutdownBackgroundWork(); } catch { }

            // 2. Timers and playback. With the backgroundMediaPlayback
            //    capability, a live MediaPlayer holds the process on its own.
            try {
                _statusTimer?.Stop();
                _typingTimer?.Stop();
                _audioPositionTimer?.Stop();
                _proxyTimer?.Stop();
                _scrollTimer?.Stop();
                _restoreTimer?.Stop();
                _videoNoteTimer?.Stop();
                _chatSearchDebounceTimer?.Stop();
            } catch { }
            var dyingPlayer = _currentAudioPlayer;
            try { StopCurrentAudio(); } catch { }
            // StopCurrentAudio does not release the player — unnecessary when
            // switching tracks, necessary here: with backgroundMediaPlayback a
            // live playback session is one more reason to keep the process.
            try { dyingPlayer?.Dispose(); } catch { }
            try { ReleaseMediaSession(); } catch { }

            // 3. TDLib: close -> authorizationStateClosed -> destroy. The
            //    _tdClosing flag takes the reading thread out of its loop (see
            //    LongPolling), and only once it is out may the client be
            //    released — destroy during an active td_json_client_receive
            //    is a race.
            bool pollingStopped = false;
            try {
                _tdCloseDeadline = DateTime.UtcNow.AddSeconds(5);
                _tdClosing = true;
                TdJson.SendUtf8(_client, "{\"@type\":\"close\"}");
                pollingStopped = await Task.WhenAny(
                    _pollingStopped.Task, Task.Delay(TimeSpan.FromSeconds(7))) == _pollingStopped.Task;
            } catch { }

            IntPtr dying = _client;
            _client = IntPtr.Zero;
            ActiveClient = IntPtr.Zero;
            // Timed out — releasing is not allowed, the thread is still reading.
            // The database was already closed by close itself, and the process
            // is about to die anyway, so leaking the pointer costs nothing.
            if (pollingStopped && dying != IntPtr.Zero)
                try { TdJson.td_json_client_destroy(dying); } catch { }

            // 4. Session mutex: the next process (the background task included)
            //    must find the database free.
            try { _tdSessionMutex?.ReleaseMutex(); } catch { }
            try { _tdSessionMutex?.Dispose(); } catch { }
            _tdSessionMutex = null;

            Application.Current.Exit();
        }

        private void UpdateArchiveUnreadBadge() {
            // Как в оригинале — число непрочитанных ЧАТОВ, а не сумма сообщений
            // в них. Чат, отмеченный "непрочитанным" вручную (UnreadCount==0),
            // тоже считается.
            int total = _archiveChatItems.Count(c => c.UnreadCount > 0 || c.IsMarkedUnread);
            if (total > 0) {
                ArchiveUnreadText.Text = total > 99 ? "99+" : total.ToString();
                ArchiveUnreadBadge.Visibility = Visibility.Visible;
                ArchiveArrow.Visibility = Visibility.Collapsed;
            } else {
                ArchiveUnreadBadge.Visibility = Visibility.Collapsed;
                ArchiveArrow.Visibility = Visibility.Visible;
            }
        }

        private void SendPhone_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(PhoneInput.Text)) return;
            PhoneButton.IsEnabled = false;
            LoginStatus.Text = Loc.T("login_sendingPhone");
            TdJson.SendUtf8(_client, "{\"@type\":\"setAuthenticationPhoneNumber\",\"phone_number\":\"" + PhoneInput.Text.Trim() + "\"}");
        }

        private void SendCode_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(CodeInput.Text)) return;
            CodeButton.IsEnabled = false;
            LoginStatus.Text = Loc.T("login_checkingCode");
            TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationCode\",\"code\":\"" + CodeInput.Text.Trim() + "\"}");
        }

        private MessageItem _selectedMessageForCopy = null;
        private MessageItem _pendingContextMsg = null; // сообщение для Reply/Forward

        private void MessageBubble_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            var border = sender as Border;
            if (border == null) return;
            _selectedMessageForCopy = border.DataContext as MessageItem;
            if (_selectedMessageForCopy == null || _selectedMessageForCopy.IsSeparator) return;
            _pendingContextMsg = _selectedMessageForCopy;

            // Показываем/скрываем пункты редактирования и удаления в зависимости от типа сообщения
            var flyout = FlyoutBase.GetAttachedFlyout(border) as MenuFlyout;
            if (flyout != null) {
                bool canEdit = _selectedMessageForCopy?.IsOutgoing == true && !string.IsNullOrEmpty(_selectedMessageForCopy?.Text);
                bool canDelete = true;
                // Собираем упоминания из текущего сообщения
                var mentions = _selectedMessageForCopy?.Entities?
                    .Where(en => en.Mention != null).ToList();
                foreach (var item in flyout.Items) {
                    if (item is MenuFlyoutItem mfi) {
                        if (mfi.Name == "MenuEdit") mfi.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;
                        if (mfi.Name == "MenuDeleteSelf" || mfi.Name == "MenuDeleteAll")
                            mfi.Visibility = canDelete ? Visibility.Visible : Visibility.Collapsed;
                        if (mfi.Name == "MenuPin") {
                            bool isPinned = _selectedMessageForCopy?.Id == _pinnedMessageId && _pinnedMessageId != 0;
                            mfi.Text = isPinned ? Loc.T("msgmenu_unpin") : Loc.T("msgmenu_pin");
                        }
                        if (mfi.Name == "MenuMention") {
                            if (mentions != null && mentions.Count > 0) {
                                mfi.Visibility = Visibility.Visible;
                                mfi.Text = mentions.Count == 1
                                    ? Loc.T("msgmenu_mention") + " " + mentions[0].Mention
                                    : Loc.T("msgmenu_mention") + " (" + mentions.Count + ")";
                            } else {
                                mfi.Visibility = Visibility.Collapsed;
                            }
                        }
                        if (mfi.Name == "MenuSaveVideo")
                            mfi.Visibility = (_selectedMessageForCopy?.IsVideo == true) ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }

            FlyoutBase.ShowAttachedFlyout(border);
        }

        private async void InlineButton_Click(object sender, RoutedEventArgs e) {
            var btn = (sender as Windows.UI.Xaml.Controls.Button)?.Tag as InlineButton;
            if (btn == null) return;

            if (!string.IsNullOrEmpty(btn.Url)) {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(btn.Url));
                return;
            }
            if (!string.IsNullOrEmpty(btn.CallbackData)) {
                // Найти msgId через Tag кнопки — он хранится в Tag как long через parent
                var button = sender as Windows.UI.Xaml.Controls.Button;
                // Идём вверх по визуальному дереву до Border с DataContext = MessageItem
                DependencyObject el = button;
                MessageItem msgItem = null;
                while (el != null) {
                    if (el is FrameworkElement fe && fe.DataContext is MessageItem mi) { msgItem = mi; break; }
                    el = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(el);
                }
                if (msgItem == null) return;
                string payload = "{\"@type\":\"getCallbackQueryAnswer\","
                    + "\"chat_id\":" + _currentChatId + ","
                    + "\"message_id\":" + msgItem.Id + ","
                    + "\"payload\":{\"@type\":\"callbackQueryPayloadData\","
                    + "\"data\":\"" + btn.CallbackData + "\"}}";
                TdJson.SendUtf8(_client, payload);
            }
        }

        private void CopyMessage_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(_selectedMessageForCopy.Text ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            _selectedMessageForCopy = null;
        }

        private void DeleteMessageSelf_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            DeleteMessages(new[] { _selectedMessageForCopy.Id }, revoke: false);
            _selectedMessageForCopy = null;
        }

        private void DeleteMessageAll_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            DeleteMessages(new[] { _selectedMessageForCopy.Id }, revoke: true);
            _selectedMessageForCopy = null;
        }

        private void DeleteMessages(long[] messageIds, bool revoke) {
            var req = new JObject {
                ["@type"] = "deleteMessages",
                ["chat_id"] = _currentChatId,
                ["message_ids"] = new JArray(messageIds),
                ["revoke"] = revoke
            };
            TdJson.SendUtf8(_client, req.ToString(Newtonsoft.Json.Formatting.None));
            // Убираем из UI сразу
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                foreach (var id in messageIds) {
                    var item = _messageItems.FirstOrDefault(m => m.Id == id);
                    if (item != null) _messageItems.Remove(item);
                    if (_messagesDict.ContainsKey(id)) _messagesDict.Remove(id);
                }
            });
        }

        private void EditMessage_Click(object sender, RoutedEventArgs e) {
            if (_selectedMessageForCopy == null) return;
            var msg = _selectedMessageForCopy;
            _selectedMessageForCopy = null;
            if (string.IsNullOrEmpty(msg.Text)) return;
            if (!msg.IsOutgoing) return; // редактировать можно только свои сообщения
            MessageInput.Text = msg.Text;
            MessageInput.SelectionStart = msg.Text.Length;
            _editingMessageId = msg.Id;
            SendButton.Content = "✓";
        }

        private long _editingMessageId = 0;
        private long _replyToMessageId = 0; // id сообщения на которое отвечаем

        private void MessageInput_TextChanged(object sender, Windows.UI.Xaml.Controls.TextChangedEventArgs e) {
            UpdateSendButtonState();
            if (_currentChatId == 0 || string.IsNullOrEmpty(MessageInput.Text)) return;
            // Отправляем chatActionTyping и перезапускаем таймер сброса
            TdJson.SendUtf8(_client, "{\"@type\":\"sendChatAction\",\"chat_id\":" + _currentChatId +
                ",\"action\":{\"@type\":\"chatActionTyping\"}}");
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        /// <summary>Клиент TDLib текущего процесса — читает BackgroundService.</summary>
        public static IntPtr ActiveClient = IntPtr.Zero;

        /// <summary>
        /// После разблокировки TDLib отдаёт весь накопившийся backlog как
        /// updateNewMessage. Без этого порога пользователь получает пачку
        /// уведомлений о сообщениях, которые он прямо сейчас и открыл.
        /// </summary>
        private const int ToastMaxAgeSeconds = 60;

        /// <summary>В фоновой догрузке — всё, что пришло за последний час.</summary>
        private const int CatchUpToastMaxAgeSeconds = 3600;

        // ---- Переключение "микрофон / отправить" и режим видеосообщения ----

        private bool _videoNoteMode = false;

        /// <summary>Пустое поле — микрофон, есть текст — кнопка отправки.</summary>
        private void UpdateSendButtonState() {
            // Во время записи не переключаем, иначе кнопка исчезнет из-под пальца.
            if (_isRecording || _isRecordingVideoNote) return;
            // В режиме правки сообщения кнопка "✓" нужна даже при пустом поле.
            bool showSend = !string.IsNullOrWhiteSpace(MessageInput.Text) || _editingMessageId != 0;
            SendButton.Visibility = showSend ? Visibility.Visible : Visibility.Collapsed;
            MicButton.Visibility  = showSend ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Пункт меню "Видеосообщение": следующая запись будет видеокружком.</summary>
        private void VideoNoteMode_Click(object sender, RoutedEventArgs e) {
            SetVideoNoteMode(true);
        }

        private void SetVideoNoteMode(bool on) {
            _videoNoteMode = on;
            RecordGlyph.Text = on ? "⏺" : "🎤";
            MicButton.Background = new Windows.UI.Xaml.Media.SolidColorBrush(
                on ? Windows.UI.Color.FromArgb(255, 0, 136, 204) : Windows.UI.Colors.Transparent);
        }

        private void RecordButton_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (_videoNoteMode) VideoNoteButton_PointerPressed(sender, e);
            else                MicButton_PointerPressed(sender, e);
        }

        private void RecordButton_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e) {
            if (_videoNoteMode) {
                VideoNoteButton_PointerReleased(sender, e);
                SetVideoNoteMode(false);
            } else {
                MicButton_PointerReleased(sender, e);
            }
        }

        private void MessageInput_Holding(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs e) {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            FlyoutBase.ShowAttachedFlyout(MessageInput);
        }

        private async void PasteToInput_Click(object sender, RoutedEventArgs e) {
            var dp = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dp.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) {
                string text = await dp.GetTextAsync();
                int pos = MessageInput.SelectionStart;
                MessageInput.Text = MessageInput.Text.Insert(pos, text);
                MessageInput.SelectionStart = pos + text.Length;
            }
        }

        private void SendPassword_Click(object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(PasswordInput.Password)) return;
            PasswordButton.IsEnabled = false;
            LoginStatus.Text = Loc.T("login_checkingPassword");
            var pwd = PasswordInput.Password.Replace("\\", "\\\\").Replace("\"", "\\\"");
            TdJson.SendUtf8(_client, "{\"@type\":\"checkAuthenticationPassword\",\"password\":\"" + pwd + "\"}");
        }
        // ---------- voice call UI ----------------------------------------

        private DispatcherTimer _callTimer;
        private DateTime _callEstablishedUtc;

        private void OnCallChanged(object sender, TdCall call) {
            // A throw here used to leave the overlay half-painted with no way out:
            // Visibility was set before the buttons were configured, so a failure
            // produced a blank screen and no diagnosis. Log and keep going instead.
            try {
                // CallChanged is raised from HandleUpdate, which already runs on
                // the UI thread via the dispatch in LongPolling.
                UpdateCallUi(call);
            } catch (Exception ex) {
                Log("call: UI update failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void OnCallControllerStateChanged(object sender, libtgvoip.CallState state) {
            // Raised on a libtgvoip thread, so this one does have to marshal.
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                if (state == libtgvoip.CallState.Established) {
                    _callEstablishedUtc = DateTime.UtcNow;
                    StartCallTimer();
                } else if (state == libtgvoip.CallState.WaitInit || state == libtgvoip.CallState.WaitInitAck) {
                    CallStatus.Text = Loc.T("callui_connecting");
                }
            });
        }

        private void UpdateCallUi(TdCall call) {
            if (call == null) return;

            switch (call.State) {
                case "callStateDiscarded":
                    BackgroundService.HideCallToast();
                    _answeredCallId = 0;
                    CallStatus.Text = Loc.T(call.WasDeclined ? "callui_declined" : "callui_ended");
                    HideCallOverlayLater();
                    return;
                case "callStateError":
                    BackgroundService.HideCallToast();
                    _answeredCallId = 0;
                    // The server rejects acceptCall with this when the two sides share
                    // no call protocol. libtgvoip speaks the legacy 2.4.4 protocol;
                    // clients that have moved to tgcalls have nothing in common with
                    // it, so "Call failed" reads like a bug rather than a limitation.
                    CallStatus.Text = call.ErrorMessage == "CALL_PROTOCOL_COMPAT_LAYER_INVALID"
                        ? Loc.T("callui_incompatible")
                        : Loc.T("callui_failed");
                    HideCallOverlayLater();
                    return;
            }

            CallPeerName.Text = ResolveUserName(call.UserId);
            CallHangupButton.Visibility = Visibility.Visible;

            // The overlay shows nothing when the window is off screen, yet with
            // background mode on the process is alive and the call still arrives.
            // Without this the call rings nowhere.
            if (!call.IsOutgoing && call.State == "callStatePending" && !BackgroundService.IsAppOnScreen) {
                BackgroundService.ShowCallToast(CallPeerName.Text);
            } else if (call.State != "callStatePending") {
                BackgroundService.HideCallToast();
            }

            // Answer is only meaningful for an incoming call that has not been
            // picked up yet; every other state leaves just the hang-up button.
            bool canAnswer = !call.IsOutgoing && call.State == "callStatePending"
                             && call.Id != _answeredCallId;
            CallAnswerButton.Visibility = canAnswer ? Visibility.Visible : Visibility.Collapsed;

            // Revealed only once everything above succeeded.
            CallOverlay.Visibility = Visibility.Visible;
            Log("call: overlay shown, answer=" + canAnswer + " name=\"" + CallPeerName.Text + "\"");

            if (call.State == "callStatePending") {
                CallStatus.Text = Loc.T(call.IsOutgoing ? (call.IsReceived ? "callui_ringing" : "callui_outgoing") : "callui_incoming");
            } else if (call.State == "callStateExchangingKeys") {
                CallStatus.Text = Loc.T("callui_exchangingKeys");
            } else if (call.IsReady) {
                CallStatus.Text = Loc.T("callui_ongoing");
                if (call.Emojis != null && call.Emojis.Count > 0) {
                    CallEmojis.Text = string.Join(" ", call.Emojis);
                }
            }
        }

        private string ResolveUserName(long userId) {
            JToken user;
            if (!_usersDict.TryGetValue(userId, out user) || user == null) return string.Empty;
            return (user["first_name"]?.ToString() + " " + user["last_name"]?.ToString()).Trim();
        }

        private void StartCallTimer() {
            if (_callTimer == null) {
                _callTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _callTimer.Tick += (s, e) => {
                    var elapsed = DateTime.UtcNow - _callEstablishedUtc;
                    CallStatus.Text = Loc.T("callui_ongoing") + "  " + elapsed.ToString(@"mm\:ss");
                };
            }
            _callTimer.Start();
        }

        private void StopCallTimer() {
            if (_callTimer != null) _callTimer.Stop();
            _callEstablishedUtc = default(DateTime);
        }

        private async void HideCallOverlayLater() {
            StopCallTimer();
            // Leave the outcome on screen briefly; dismissing instantly makes a
            // declined or failed call look like nothing happened.
            await Task.Delay(1500);
            CallOverlay.Visibility = Visibility.Collapsed;
            CallEmojis.Text = string.Empty;
        }

        /// <summary>
        /// Calls are person-to-person, so the button appears only for a private
        /// chat that is not Saved Messages, and never inside a comment thread.
        /// </summary>
        private void UpdateChatCallButton(long chatId, long threadId) {
            var visible = false;
            try {
                if (threadId == 0) {
                    JToken raw;
                    if (_rawChatsDict.TryGetValue(chatId, out raw) && raw != null) {
                        var type = raw["type"];
                        visible = (string)type?["@type"] == "chatTypePrivate"
                                  && (long?)type["user_id"] != _myUserId;
                    }
                }
            } catch (Exception ex) {
                Log("call: chat button check failed: " + ex.Message);
            }
            ChatCallButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChatCallButton_Click(object sender, RoutedEventArgs e) {
            try {
                JToken raw;
                if (!_rawChatsDict.TryGetValue(_currentChatId, out raw) || raw == null) return;
                var userId = (long?)raw["type"]?["user_id"] ?? 0;
                if (userId == 0) return;
                StartCallWithUser(userId);
            } catch (Exception ex) {
                Log("call: could not start outgoing call: " + ex.Message);
            }
        }

        private void CallAnswer_Click(object sender, RoutedEventArgs e) {
            Calls.AcceptIncomingCall();
        }

        private void CallHangup_Click(object sender, RoutedEventArgs e) {
            Calls.HangUp();
        }

        /// <summary>Places an outgoing call. No caller yet - see notes in step 4.</summary>
        private void StartCallWithUser(long userId) {
            _callEstablishedUtc = default(DateTime);
            CallEmojis.Text = string.Empty;
            CallOverlay.Visibility = Visibility.Visible;
            CallPeerName.Text = ResolveUserName(userId);
            CallStatus.Text = Loc.T("callui_outgoing");
            CallAnswerButton.Visibility = Visibility.Collapsed;
            Calls.StartOutgoingCall(userId);
        }
    }
}
