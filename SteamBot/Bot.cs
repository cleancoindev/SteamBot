using System;
using System.Threading.Tasks;
using System.Web;
using System.Net;
using System.Text;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.ComponentModel;
using SteamBot.SteamGroups;
using SteamKit2;
using SteamTrade;
using SteamKit2.Internal;
using SteamTrade.TradeOffer;
using SteamBot.Logging;
using SteamTrade.Inventory;

namespace SteamBot
{
    public class Bot
    {
        public string BotControlClass;
        // If the bot is logged in fully or not.  This is only set
        // when it is.
        public bool IsLoggedIn { get; private set; }

        // The bot's display name.  Changing this does not mean that
        // the bot's name will change.
        public string DisplayName { get; private set; }

        // The response to all chat messages sent to it.
        public string ChatResponse;

        // A list of SteamIDs that this bot recognizes as admins.
        public ulong[] Admins;
        public SteamFriends SteamFriends;
        public SteamClient SteamClient;
        public SteamTrading SteamTrade;
        public SteamUser SteamUser;
        public SteamGameCoordinator SteamGameCoordinator;
        public SteamNotifications SteamNotifications;

        // The current trade; if the bot is not in a trade, this is
        // null.
        public Trade CurrentTrade;

        public bool IsDebugMode = false;

        // The log for the bot.  This logs with the bot's display name.
        public Log log;
        public string[] InventoriesToLoad { get; private set; }
        public List<CInventory> MyLoadedInventories = new List<CInventory>();
        private string logFile;

        public delegate UserHandler UserHandlerCreator(Bot bot, SteamID id);
        public UserHandlerCreator CreateHandler;
        Dictionary<ulong, UserHandler> userHandlers = new Dictionary<ulong, UserHandler>();

        private List<SteamID> friends;
        public IEnumerable<SteamID> FriendsList
        {
            get
            {
                CreateFriendsListIfNecessary();
                return friends;
            }
        }

        // The maximum amount of time the bot will trade for.
        public int MaximumTradeTime { get; private set; }

        // The maximum amount of time the bot will wait in between
        // trade actions.
        public int MaximiumActionGap { get; private set; }

        //The current game that the bot is playing, for posterity.
        public int CurrentGame = 0;

        // The Steam Web API key.
        public string ApiKey { get; private set; }

        public SteamWeb SteamWeb { get; private set; }

        // The prefix put in the front of the bot's display name.
        string DisplayNamePrefix;

        // Log level to use for this bot
        LogLevel ConsoleLogLevel;
        LogLevel FileLogLevel;

        // The number, in milliseconds, between polls for the trade.
        int TradePollingInterval;

        public string MyUserNonce;
        public string MyUniqueId;

        bool CookiesAreInvalid = true;

        bool isprocess;
        public bool IsRunning = false;

        public string AuthCode { get; set; }

        SteamUser.LogOnDetails logOnDetails;

        TradeManager tradeManager;
        private TradeOfferManager tradeOfferManager;

        private BackgroundWorker backgroundWorker;

        public Bot(Configuration.BotInfo config, string apiKey, UserHandlerCreator handlerCreator, bool debug = false, bool process = false)
        {
            logOnDetails = new SteamUser.LogOnDetails
            {
                Username = config.Username,
                Password = config.Password
            };
            DisplayName = config.DisplayName;
            ChatResponse = config.ChatResponse;
            MaximumTradeTime = config.MaximumTradeTime;
            MaximiumActionGap = config.MaximumActionGap;
            DisplayNamePrefix = config.DisplayNamePrefix;
            TradePollingInterval = config.TradePollingInterval <= 100 ? 800 : config.TradePollingInterval;
            Admins = config.Admins;
            InventoriesToLoad = config.Inventories;
            this.ApiKey = !String.IsNullOrEmpty(config.ApiKey) ? config.ApiKey : apiKey;
            this.isprocess = process;

            try
            {
                if(config.LogLevel != null)
                {
                    ConsoleLogLevel = (LogLevel) Enum.Parse(typeof(LogLevel), config.LogLevel, true);
                    Console.WriteLine(
                        @"(Console) LogLevel configuration parameter used in bot {0} is depreciated and may be removed in future versions. Please use ConsoleLogLevel instead.",
                        DisplayName);
            }
                else
                {
                    ConsoleLogLevel = (LogLevel)Enum.Parse(typeof(LogLevel), config.ConsoleLogLevel, true);
                }
            }
            catch (ArgumentException)
            {
                Console.WriteLine(@"(Console) ConsoleLogLevel invalid or unspecified for bot {0}. Defaulting to ""Info""", DisplayName);
                ConsoleLogLevel = LogLevel.Info;
            }

            try
            {
                FileLogLevel = (LogLevel)Enum.Parse(typeof(LogLevel), config.FileLogLevel, true);
            }
            catch (ArgumentException)
            {
                Console.WriteLine(@"(Console) FileLogLevel invalid or unspecified for bot {0}. Defaulting to ""Info""", DisplayName);
                FileLogLevel = LogLevel.Info;
            }

            logFile = config.LogFile;
            CreateHandler = handlerCreator;
            BotControlClass = config.BotControlClass;
            SteamWeb = new SteamWeb();
            CreateLog();

            // Hacking around https
            ServicePointManager.ServerCertificateValidationCallback += SteamWeb.ValidateRemoteCertificate;

            log.Debug("Initializing Steam Bot...");
            SteamClient = new SteamClient();
            SteamClient.AddHandler(new SteamNotifications());
            SteamTrade = SteamClient.GetHandler<SteamTrading>();
            SteamUser = SteamClient.GetHandler<SteamUser>();
            SteamFriends = SteamClient.GetHandler<SteamFriends>();
            SteamGameCoordinator = SteamClient.GetHandler<SteamGameCoordinator>();
            SteamNotifications = SteamClient.GetHandler<SteamNotifications>();

            GetUserHandler(new SteamID((ulong)0)).OnBotCreated();

            backgroundWorker = new BackgroundWorker { WorkerSupportsCancellation = true };
            backgroundWorker.DoWork += BackgroundWorkerOnDoWork;
            backgroundWorker.RunWorkerCompleted += BackgroundWorkerOnRunWorkerCompleted;
            backgroundWorker.RunWorkerAsync();
        }

        private void CreateLog()
        {
            if(log == null)
                log = new Log(DisplayName, true, new ConsoleLogger(ConsoleLogLevel), new FileLogger(FileLogLevel, logFile));
        }

        private void DisposeLog()
        {
            if(log != null)
            {
                log.Dispose();
                log = null;
            }
        }

        private void CreateFriendsListIfNecessary()
        {
            if (friends != null)
                return;

            friends = new List<SteamID>();
            for (int i = 0; i < SteamFriends.GetFriendCount(); i++)
            {
                friends.Add(SteamFriends.GetFriendByIndex(i));
            }
        }

        /// <summary>
        /// Occurs when the bot needs the SteamGuard authentication code.
        /// </summary>
        /// <remarks>
        /// Return the code in <see cref="SteamGuardRequiredEventArgs.SteamGuard"/>
        /// </remarks>
        public event EventHandler<SteamGuardRequiredEventArgs> OnSteamGuardRequired;

        /// <summary>
        /// Starts the callback thread and connects to Steam via SteamKit2.
        /// </summary>
        /// <remarks>
        /// THIS NEVER RETURNS.
        /// </remarks>
        /// <returns><c>true</c>. See remarks</returns>
        public bool StartBot()
        {
            CreateLog();
            IsRunning = true;

            log.Info("Connecting...");

            if (!backgroundWorker.IsBusy)
                // background worker is not running
                backgroundWorker.RunWorkerAsync();

            SteamClient.Connect();

            log.Success("Done Loading Bot!");

            return true; // never get here
        }

        /// <summary>
        /// Disconnect from the Steam network and stop the callback
        /// thread.
        /// </summary>
        public void StopBot()
        {
            IsRunning = false;

            log.Debug("Trying to shut down bot thread.");
            SteamClient.Disconnect();

            backgroundWorker.CancelAsync();
            userHandlers.Clear();
            DisposeLog();
        }

        /// <summary>
        /// Creates a new trade with the given partner.
        /// </summary>
        /// <returns>
        /// <c>true</c>, if trade was opened,
        /// <c>false</c> if there is another trade that must be closed first.
        /// </returns>
        public bool OpenTrade(SteamID other)
        {
            if (CurrentTrade != null || CheckCookies() == false)
                return false;

            SteamTrade.Trade(other);

            return true;
        }

        /// <summary>
        /// Closes the current active trade.
        /// </summary>
        public void CloseTrade()
        {
            if (CurrentTrade == null)
                return;

            UnsubscribeTrade(GetUserHandler(CurrentTrade.OtherSID), CurrentTrade);

            tradeManager.StopTrade();

            CurrentTrade = null;
        }

        void OnTradeTimeout(object sender, EventArgs args)
        {
            // ignore event params and just null out the trade.
            GetUserHandler(CurrentTrade.OtherSID).OnTradeTimeout();
        }

        /// <summary>
        /// Create a new trade offer with the specified partner
        /// </summary>
        /// <param name="other">SteamId of the partner</param>
        /// <returns></returns>
        public TradeOffer NewTradeOffer(SteamID other)
        {
            return tradeOfferManager.NewOffer(other);
        }

        /// <summary>
        /// Try to get a specific trade offer using the offerid
        /// </summary>
        /// <param name="offerId"></param>
        /// <param name="tradeOffer"></param>
        /// <returns></returns>
        public bool TryGetTradeOffer(string offerId, out TradeOffer tradeOffer)
        {
            return tradeOfferManager.GetOffer(offerId, out tradeOffer);
        }

        public void HandleBotCommand(string command)
        {
            try
            {
                GetUserHandler(SteamClient.SteamID).OnBotCommand(command);
            }
            catch (ObjectDisposedException e)
            {
                // Writing to console because odds are the error was caused by a disposed log.
                Console.WriteLine(string.Format("Exception caught in BotCommand Thread: {0}", e));
                if (!this.IsRunning)
                {
                    Console.WriteLine("The Bot is no longer running and could not write to the log. Try Starting this bot first.");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(string.Format("Exception caught in BotCommand Thread: {0}", e));
            }
        }

        bool HandleTradeSessionStart(SteamID other)
        {
            if (CurrentTrade != null)
                return false;

            try
            {
                tradeManager.InitializeTrade(SteamUser.SteamID, other);
                CurrentTrade = tradeManager.CreateTrade(SteamUser.SteamID, other);
                CurrentTrade.OnClose += CloseTrade;
                SubscribeTrade(CurrentTrade, GetUserHandler(other));

                tradeManager.StartTradeThread(CurrentTrade);
                return true;
            }
            catch (SteamTrade.Exceptions.InventoryFetchException ie)
            {
                // we shouldn't get here because the inv checks are also
                // done in the TradeProposedCallback handler.
                string response = String.Empty;

                if (ie.FailingSteamId.ConvertToUInt64() == other.ConvertToUInt64())
                {
                    response = "Trade failed. Could not correctly fetch your backpack. Either the inventory is inaccessible or your backpack is private.";
                }
                else
                {
                    response = "Trade failed. Could not correctly fetch my backpack.";
                }

                SteamFriends.SendChatMessage(other,
                                             EChatEntryType.ChatMsg,
                                             response);

                log.Info ("Bot sent other: {0}", response);

                CurrentTrade = null;
                return false;
            }
        }

        public void SetGamePlaying(int id)
        {
            var gamePlaying = new SteamKit2.ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

            if (id != 0)
                gamePlaying.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                {
                    game_id = new GameID(id),
                });

            SteamClient.Send(gamePlaying);

            CurrentGame = id;
        }

        void HandleSteamMessage(ICallbackMsg msg)
        {
            log.Debug(msg.ToString());

            #region Login
            msg.Handle<SteamClient.ConnectedCallback>(callback =>
            {
                log.Debug ("Connection Callback: {0}", callback.Result);

                if (callback.Result == EResult.OK)
                {
                    UserLogOn();
                }
                else
                {
                    log.Error("Failed to connect to Steam Community, trying again...");
                    SteamClient.Connect();
                }

            });

            msg.Handle<SteamUser.LoggedOnCallback>(callback =>
            {
                log.Debug("Logged On Callback: {0}", callback.Result);

                if (callback.Result == EResult.OK)
                {
                    MyUserNonce = callback.WebAPIUserNonce;
                }
                else
                {
                    log.Error("Login Error: {0}", callback.Result);
                }

                if (callback.Result == EResult.AccountLogonDenied)
                {
                    log.Interface("This account is SteamGuard enabled. Enter the code via the `auth' command.");

                    // try to get the steamguard auth code from the event callback
                    var eva = new SteamGuardRequiredEventArgs();
                    FireOnSteamGuardRequired(eva);
                    if (!String.IsNullOrEmpty(eva.SteamGuard))
                        logOnDetails.AuthCode = eva.SteamGuard;
                    else
                        logOnDetails.AuthCode = Console.ReadLine();
                }

                if (callback.Result == EResult.InvalidLoginAuthCode)
                {
                    log.Interface("The given SteamGuard code was invalid. Try again using the `auth' command.");
                    logOnDetails.AuthCode = Console.ReadLine();
                }
            });

            msg.Handle<SteamUser.LoginKeyCallback>(callback =>
            {
                MyUniqueId = callback.UniqueID.ToString();

                UserWebLogOn();
                LoadMyInventoriesAsync();
                SteamFriends.SetPersonaName(DisplayNamePrefix + DisplayName);
                SteamFriends.SetPersonaState(EPersonaState.Online);

                log.Success("Steam Bot Logged In Completely!");

                GetUserHandler(SteamClient.SteamID).OnLoginCompleted();
            });

            msg.Handle<SteamUser.WebAPIUserNonceCallback>(webCallback =>
            {
                log.Debug("Received new WebAPIUserNonce.");

                if (webCallback.Result == EResult.OK)
                {
                    MyUserNonce = webCallback.Nonce;
                    UserWebLogOn();
                }
                else
                {
                    log.Error("WebAPIUserNonce Error: " + webCallback.Result);
                }
            });

            msg.Handle<SteamUser.UpdateMachineAuthCallback>(
                authCallback => OnUpdateMachineAuthCallback(authCallback)
            );
            #endregion

            #region Friends
            msg.Handle<SteamFriends.FriendsListCallback>(callback =>
            {
                foreach (SteamFriends.FriendsListCallback.Friend friend in callback.FriendList)
                {
                    switch (friend.SteamID.AccountType)
                    {
                        case EAccountType.Clan:
                            if (friend.Relationship == EFriendRelationship.RequestRecipient)
                            {
                                if (GetUserHandler(friend.SteamID).OnGroupAdd())
                                {
                                    AcceptGroupInvite(friend.SteamID);
                                }
                                else
                                {
                                    DeclineGroupInvite(friend.SteamID);
                                }
                            }
                            break;
                        default:
                            CreateFriendsListIfNecessary();
                            if (friend.Relationship == EFriendRelationship.None)
                            {
                                friends.Remove(friend.SteamID);
                                GetUserHandler(friend.SteamID).OnFriendRemove();
                                RemoveUserHandler(friend.SteamID);
                            }
                            else if (friend.Relationship == EFriendRelationship.RequestRecipient)
                            {
                                if (GetUserHandler(friend.SteamID).OnFriendAdd())
                                {
                                    if(!friends.Contains(friend.SteamID))
                                    {
                                        friends.Add(friend.SteamID);
                                    }
                                    else
                                    {
                                        log.Error("Friend was added who was already in friends list: " + friend.SteamID);
                                    }
                                    SteamFriends.AddFriend(friend.SteamID);
                                }
                                else
                                {
                                    SteamFriends.RemoveFriend(friend.SteamID);
                                    RemoveUserHandler(friend.SteamID);
                                }
                            }
                            break;
                    }
                }
            });


            msg.Handle<SteamFriends.FriendMsgCallback>(callback =>
            {
                EChatEntryType type = callback.EntryType;

                if (callback.EntryType == EChatEntryType.ChatMsg)
                {
                    log.Info ("Chat Message from {0}: {1}",
                                         SteamFriends.GetFriendPersonaName(callback.Sender),
                                         callback.Message
                                         );
                    GetUserHandler(callback.Sender).OnMessageHandler(callback.Message, type);
                }
            });
            #endregion

            #region Group Chat
            msg.Handle<SteamFriends.ChatMsgCallback>(callback =>
            {
                GetUserHandler(callback.ChatterID).OnChatRoomMessage(callback.ChatRoomID, callback.ChatterID, callback.Message);
            });
            #endregion

            #region Trading
            msg.Handle<SteamTrading.SessionStartCallback>(callback =>
            {
                bool started = HandleTradeSessionStart(callback.OtherClient);

                if (!started)
                    log.Error("Could not start the trade session.");
                else
                    log.Debug("SteamTrading.SessionStartCallback handled successfully. Trade Opened.");
            });

            msg.Handle<SteamTrading.TradeProposedCallback>(callback =>
            {
                if (CheckCookies() == false)
                {
                    SteamTrade.RespondToTrade(callback.TradeID, false);
                    return;
                }

                try
                {
                    tradeManager.InitializeTrade(SteamUser.SteamID, callback.OtherClient);
                }
                catch (WebException we)
                {
                    SteamFriends.SendChatMessage(callback.OtherClient,
                             EChatEntryType.ChatMsg,
                             "Trade error: " + we.Message);

                    SteamTrade.RespondToTrade(callback.TradeID, false);
                    return;
                }
                catch (Exception)
                {
                    SteamFriends.SendChatMessage(callback.OtherClient,
                             EChatEntryType.ChatMsg,
                             "Trade declined. Could not correctly fetch your backpack.");

                    SteamTrade.RespondToTrade(callback.TradeID, false);
                    return;
                }

                //if (tradeManager.OtherInventory.IsPrivate)
                //{
                //    SteamFriends.SendChatMessage(callback.OtherClient, 
                //                                 EChatEntryType.ChatMsg,
                //                                 "Trade declined. Your backpack cannot be private.");

                //    SteamTrade.RespondToTrade (callback.TradeID, false);
                //    return;
                //}

                if (CurrentTrade == null && GetUserHandler(callback.OtherClient).OnTradeRequest())
                    SteamTrade.RespondToTrade(callback.TradeID, true);
                else
                    SteamTrade.RespondToTrade(callback.TradeID, false);
            });

            msg.Handle<SteamTrading.TradeResultCallback>(callback =>
            {
                if (callback.Response == EEconTradeResponse.Accepted)
                {
                    log.Debug("Trade Status: {0}", callback.Response);
                    log.Info("Trade Accepted!");
                    GetUserHandler(callback.OtherClient).OnTradeRequestReply(true, callback.Response.ToString());
                }
                else
                {
                    log.Warn("Trade failed: {0}", callback.Response);
                    CloseTrade();
                    GetUserHandler(callback.OtherClient).OnTradeRequestReply(false, callback.Response.ToString());
                }

            });
            #endregion

            #region Disconnect
            msg.Handle<SteamUser.LoggedOffCallback>(callback =>
            {
                IsLoggedIn = false;
                log.Warn("Logged off Steam.  Reason: {0}", callback.Result);
            });

            msg.Handle<SteamClient.DisconnectedCallback>(callback =>
            {
                if(IsLoggedIn)
                {
                IsLoggedIn = false;
                CloseTrade();
                log.Warn("Disconnected from Steam Network!");
                }

                SteamClient.Connect();
            });
            #endregion

            #region Notifications
            msg.Handle<SteamBot.SteamNotifications.NotificationCallback>(callback =>
            {
                //currently only appears to be of trade offer
                if (callback.Notifications.Count != 0)
                {
                    foreach (var notification in callback.Notifications)
                    {
                        log.Info(notification.UserNotificationType + " notification");
                    }
                }

                // Get offers only if cookies are valid
                if (CheckCookies())
                    tradeOfferManager.GetOffers();
            });

            msg.Handle<SteamBot.SteamNotifications.CommentNotificationCallback>(callback =>
            {
                //various types of comment notifications on profile/activity feed etc
                //log.Info("received CommentNotificationCallback");
                //log.Info("New Commments " + callback.CommentNotifications.CountNewComments);
                //log.Info("New Commments Owners " + callback.CommentNotifications.CountNewCommentsOwner);
                //log.Info("New Commments Subscriptions" + callback.CommentNotifications.CountNewCommentsSubscriptions);
            });
            #endregion
        }

        void UserLogOn()
        {
            // get sentry file which has the machine hw info saved 
            // from when a steam guard code was entered
            Directory.CreateDirectory(System.IO.Path.Combine(System.Windows.Forms.Application.StartupPath, "sentryfiles"));
            FileInfo fi = new FileInfo(System.IO.Path.Combine("sentryfiles", String.Format("{0}.sentryfile", logOnDetails.Username)));

            if (fi.Exists && fi.Length > 0)
                logOnDetails.SentryFileHash = SHAHash(File.ReadAllBytes(fi.FullName));
            else
                logOnDetails.SentryFileHash = null;

            SteamUser.LogOn(logOnDetails);
        }

        void UserWebLogOn()
        {
            do
            {
                IsLoggedIn = SteamWeb.Authenticate(MyUniqueId, SteamClient, MyUserNonce);

                if(!IsLoggedIn)
                {
                    log.Warn("Authentication failed, retrying in 2s...");
                    Thread.Sleep(2000);
                }
            } while(!IsLoggedIn);

                    log.Success("User Authenticated!");

                    tradeManager = new TradeManager(ApiKey, SteamWeb, InventoriesToLoad);
                    tradeManager.SetTradeTimeLimits(MaximumTradeTime, MaximiumActionGap, TradePollingInterval);
                    tradeManager.OnTimeout += OnTradeTimeout;

                    tradeOfferManager = new TradeOfferManager(ApiKey, SteamWeb);
                    SubscribeTradeOffer(tradeOfferManager);

                    CookiesAreInvalid = false;

                    // Success, check trade offers which we have received while we were offline
                    tradeOfferManager.GetOffers();
                }

        /// <summary>
        /// Checks if sessionId and token cookies are still valid.
        /// Sets cookie flag if they are invalid.
        /// </summary>
        /// <returns>true if cookies are valid; otherwise false</returns>
        bool CheckCookies()
        {
            // We still haven't re-authenticated
            if (CookiesAreInvalid)
                return false;

            try
            {
                if (!SteamWeb.VerifyCookies())
                {
                    // Cookies are no longer valid
                    log.Warn("Cookies are invalid. Need to re-authenticate.");
                    CookiesAreInvalid = true;
                    SteamUser.RequestWebAPIUserNonce();
                    return false;
                }
                }
            catch
            {
                // Even if exception is caught, we should still continue.
                log.Warn("Cookie check failed. http://steamcommunity.com is possibly down.");
            }

                return true;
            }

        UserHandler GetUserHandler(SteamID sid)
        {
            if (!userHandlers.ContainsKey(sid))
            {
                userHandlers[sid.ConvertToUInt64()] = CreateHandler(this, sid);
            }
            return userHandlers[sid.ConvertToUInt64()];
        }

        void RemoveUserHandler(SteamID sid)
        {
            if (userHandlers.ContainsKey(sid))
            {
                userHandlers.Remove(sid);
            }
        }

        static byte[] SHAHash(byte[] input)
        {
            SHA1Managed sha = new SHA1Managed();

            byte[] output = sha.ComputeHash(input);

            sha.Clear();

            return output;
        }

        void OnUpdateMachineAuthCallback(SteamUser.UpdateMachineAuthCallback machineAuth)
        {
            byte[] hash = SHAHash(machineAuth.Data);

            Directory.CreateDirectory(System.IO.Path.Combine(System.Windows.Forms.Application.StartupPath, "sentryfiles"));

            File.WriteAllBytes(System.IO.Path.Combine("sentryfiles", String.Format("{0}.sentryfile", logOnDetails.Username)), machineAuth.Data);

            var authResponse = new SteamUser.MachineAuthDetails
            {
                BytesWritten = machineAuth.BytesToWrite,
                FileName = machineAuth.FileName,
                FileSize = machineAuth.BytesToWrite,
                Offset = machineAuth.Offset,

                SentryFileHash = hash, // should be the sha1 hash of the sentry file we just wrote

                OneTimePassword = machineAuth.OneTimePassword, // not sure on this one yet, since we've had no examples of steam using OTPs

                LastError = 0, // result from win32 GetLastError
                Result = EResult.OK, // if everything went okay, otherwise ~who knows~
                JobID = machineAuth.JobID, // so we respond to the correct server job
            };

            // send off our response
            SteamUser.SendMachineAuthResponse(authResponse);
        }

        public void TradeOfferRouter(TradeOffer offer)
        {
            if (offer.OfferState == TradeOfferState.TradeOfferStateActive)
            {
                GetUserHandler(offer.PartnerSteamId).OnNewTradeOffer(offer);
            }
        }
        public void SubscribeTradeOffer(TradeOfferManager tradeOfferManager)
        {
            tradeOfferManager.OnNewTradeOffer += TradeOfferRouter;
        }

        /// <summary>
        /// This loads the other user's inventories from the bot's config.
        /// </summary>
        public void LoadMyInventories()
        {
            MyLoadedInventories = CInventory.FetchInventories(SteamWeb, SteamUser.SteamID, InventoriesToLoad) as List<CInventory>;
        }

        /// <summary>
        /// This loads the other user's inventories from the bot's config.
        /// </summary>
        public void LoadMyInventoriesAsync()
        {
            CInventory.FetchInventoriesAsync(SteamWeb, SteamUser.SteamID, InventoriesToLoad, delegate(CInventory inventory)
            {
                if (inventory.InventoryLoaded)
                    MyLoadedInventories.Add(inventory);
            });
        }

        //todo: should unsubscribe eventually...
        public void UnsubscribeTradeOffer(TradeOfferManager tradeOfferManager)
        {
            tradeOfferManager.OnNewTradeOffer -= TradeOfferRouter;
        }

        /// <summary>
        /// Subscribes all listeners of this to the trade.
        /// </summary>
        public void SubscribeTrade(Trade trade, UserHandler handler)
        {
            trade.OnSuccess += handler.OnTradeSuccess;
            trade.OnClose += handler.OnTradeClose;
            trade.OnError += handler.OnTradeError;
            trade.OnStatusError += handler.OnStatusError;
            //trade.OnTimeout += OnTradeTimeout;
            trade.OnAfterInit += handler.OnTradeInit;
            trade.OnUserAddItem += handler.OnTradeAddItem;
            trade.OnUserRemoveItem += handler.OnTradeRemoveItem;
            trade.OnMessage += handler.OnTradeMessageHandler;
            trade.OnUserSetReady += handler.OnTradeReadyHandler;
            trade.OnUserAccept += handler.OnTradeAcceptHandler;
        }

        /// <summary>
        /// Unsubscribes all listeners of this from the current trade.
        /// </summary>
        public void UnsubscribeTrade(UserHandler handler, Trade trade)
        {
            trade.OnSuccess -= handler.OnTradeSuccess;
            trade.OnClose -= handler.OnTradeClose;
            trade.OnError -= handler.OnTradeError;
            trade.OnStatusError -= handler.OnStatusError;
            //Trade.OnTimeout -= OnTradeTimeout;
            trade.OnAfterInit -= handler.OnTradeInit;
            trade.OnUserAddItem -= handler.OnTradeAddItem;
            trade.OnUserRemoveItem -= handler.OnTradeRemoveItem;
            trade.OnMessage -= handler.OnTradeMessageHandler;
            trade.OnUserSetReady -= handler.OnTradeReadyHandler;
            trade.OnUserAccept -= handler.OnTradeAcceptHandler;
        }

        #region Background Worker Methods

        private void BackgroundWorkerOnRunWorkerCompleted(object sender, RunWorkerCompletedEventArgs runWorkerCompletedEventArgs)
        {
            if (runWorkerCompletedEventArgs.Error != null)
            {
                Exception ex = runWorkerCompletedEventArgs.Error;

                log.Error("Unhandled exceptions in bot {0} callback thread: {1} {2}",
                      DisplayName,
                      Environment.NewLine,
                      ex);

                log.Info("This bot died. Stopping it..");
                //backgroundWorker.RunWorkerAsync();
                //Thread.Sleep(10000);
                StopBot();
                //StartBot();
            }

            log.Dispose();
        }

        private void BackgroundWorkerOnDoWork(object sender, DoWorkEventArgs doWorkEventArgs)
        {
            ICallbackMsg msg;

            while (!backgroundWorker.CancellationPending)
            {
                try
                {
                    msg = SteamClient.WaitForCallback(true);
                    HandleSteamMessage(msg);
                }
                catch (WebException e)
                {
                    log.Error("URI: {0} >> {1}", (e.Response != null && e.Response.ResponseUri != null ? e.Response.ResponseUri.ToString() : "unknown"), e.ToString());
                    System.Threading.Thread.Sleep(45000);//Steam is down, retry in 45 seconds.
                }
                catch (Exception e)
                {
                    log.Error(e.ToString());
                    log.Warn("Restarting bot...");
                }
            }
        }

        #endregion Background Worker Methods

        private void FireOnSteamGuardRequired(SteamGuardRequiredEventArgs e)
        {
            // Set to null in case this is another attempt
            this.AuthCode = null;

            EventHandler<SteamGuardRequiredEventArgs> handler = OnSteamGuardRequired;
            if (handler != null)
                handler(this, e);
            else
            {
                while (true)
                {
                    if (this.AuthCode != null)
                    {
                        e.SteamGuard = this.AuthCode;
                        break;
                    }

                    Thread.Sleep(5);
                }
            }
        }

        #region Group Methods

        /// <summary>
        /// Accepts the invite to a Steam Group
        /// </summary>
        /// <param name="group">SteamID of the group to accept the invite from.</param>
        private void AcceptGroupInvite(SteamID group)
        {
            var AcceptInvite = new ClientMsg<CMsgGroupInviteAction>((int)EMsg.ClientAcknowledgeClanInvite);

            AcceptInvite.Body.GroupID = group.ConvertToUInt64();
            AcceptInvite.Body.AcceptInvite = true;

            this.SteamClient.Send(AcceptInvite);

        }

        /// <summary>
        /// Declines the invite to a Steam Group
        /// </summary>
        /// <param name="group">SteamID of the group to decline the invite from.</param>
        private void DeclineGroupInvite(SteamID group)
        {
            var DeclineInvite = new ClientMsg<CMsgGroupInviteAction>((int)EMsg.ClientAcknowledgeClanInvite);

            DeclineInvite.Body.GroupID = group.ConvertToUInt64();
            DeclineInvite.Body.AcceptInvite = false;

            this.SteamClient.Send(DeclineInvite);
        }

        /// <summary>
        /// Invites a use to the specified Steam Group
        /// </summary>
        /// <param name="user">SteamID of the user to invite.</param>
        /// <param name="groupId">SteamID of the group to invite the user to.</param>
        public void InviteUserToGroup(SteamID user, SteamID groupId)
        {
            var InviteUser = new ClientMsg<CMsgInviteUserToGroup>((int)EMsg.ClientInviteUserToClan);

            InviteUser.Body.GroupID = groupId.ConvertToUInt64();
            InviteUser.Body.Invitee = user.ConvertToUInt64();
            InviteUser.Body.UnknownInfo = true;

            this.SteamClient.Send(InviteUser);
        }

        #endregion
    }
}
