﻿extern alias JetBrainsAnnotations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Cysharp.Threading.Tasks;
using Dummy.ConfigurationEx;
using Dummy.Extensions;
using Dummy.Models;
using Dummy.Users;
using JetBrainsAnnotations::JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using MoreLinq;
using OpenMod.API.Plugins;
using OpenMod.API.Users;
using OpenMod.Core.Helpers;
using OpenMod.Core.Localization;
using OpenMod.Core.Users;
using OpenMod.Unturned.Users;
using SDG.NetTransport;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Color = System.Drawing.Color;
using UColor = UnityEngine.Color;

namespace Dummy.Services
{
    [UsedImplicitly]
    public class DummyUserProvider : IUserProvider, IAsyncDisposable
    {
        private readonly IPluginAccessor<Dummy> m_PluginAccessor;
        private readonly IUserDataStore m_UserDataStore;
        private readonly ILogger<DummyUserProvider> m_Logger;
        private readonly ILoggerFactory m_LoggerFactory;
        private readonly ILifetimeScope m_LifetimeScope;

        private UnturnedUserProvider UserProvider => m_LifetimeScope.Resolve<IUserManager>().UserProviders
            .OfType<UnturnedUserProvider>().FirstOrDefault()!;

        public HashSet<DummyUser> DummyUsers { get; }

        private bool m_Disposed;
        private bool m_IsShuttingDown;

        public DummyUserProvider(IPluginAccessor<Dummy> pluginAccessor, IUserDataStore userDataStore,
            ILogger<DummyUserProvider> logger, ILoggerFactory loggerFactory, ILifetimeScope lifetimeScope)
        {
            m_PluginAccessor = pluginAccessor;
            m_UserDataStore = userDataStore;
            m_Logger = logger;
            m_LoggerFactory = loggerFactory;
            m_LifetimeScope = lifetimeScope;
            DummyUsers = new();

            Provider.onCommenceShutdown += ProviderOnonCommenceShutdown;
            Provider.onServerDisconnected += OnServerDisconnected;

            AsyncHelper.Schedule("Do not auto kick a dummies", DontAutoKickTask);
        }

        private void OnServerDisconnected(CSteamID steamID)
        {
            var dummy = DummyUsers.FirstOrDefault(x => x.SteamId == steamID);
            if (dummy == null)
            {
                return;
            }

            if (DummyUsers.Remove(dummy))
            {
                dummy.Actions.Enabled = false;
                dummy.Simulation.Enabled = false;

                // Because DummyUser.DisposeAsync() calls Session.DisconnectAsync with will call Provider.kick it will throw exception that clients list was modified in kick method.
                // So we will dispose it on frame later. But simulation and actions will be already disabled.
                DelayDispose(dummy).Forget();
            }
        }

        private static async UniTaskVoid DelayDispose(DummyUser dummy)
        {
            await UniTask.NextFrame();
            await dummy.DisposeAsync();
        }

        private void ProviderOnonCommenceShutdown()
        {
            m_IsShuttingDown = true;
        }

        public bool SupportsUserType(string userType)
        {
            return userType.Equals(KnownActorTypes.Player, StringComparison.OrdinalIgnoreCase);
        }

        public Task<IUser?> FindUserAsync(string userType, string searchString, UserSearchMode searchMode)
        {
            if (!SupportsUserType(userType))
            {
                return Task.FromResult<IUser?>(null);
            }

            DummyUser? dummyUser = null;
            var confidence = 0;

            foreach (var user in DummyUsers)
            {
                switch (searchMode)
                {
                    case UserSearchMode.FindByNameOrId:
                    case UserSearchMode.FindById:
                        if (user.Id.Equals(searchString, StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult<IUser?>(user);
                        }

                        if (searchMode == UserSearchMode.FindByNameOrId)
                        {
                            goto case UserSearchMode.FindByName;
                        }
                        break;

                    case UserSearchMode.FindByName:
                        var currentConfidence = NameConfidence(user.DisplayName, searchString, confidence);
                        if (currentConfidence > confidence)
                        {
                            dummyUser = user;
                            confidence = currentConfidence;
                        }
                        break;
                    default:
                        return Task.FromException<IUser?>(new ArgumentOutOfRangeException(nameof(searchMode), searchMode,
                            null));
                }
            }

            return Task.FromResult<IUser?>(dummyUser);
        }

        private static int NameConfidence(string userName, string searchName, int currentConfidence = -1)
        {
            switch (currentConfidence)
            {
                case 2:
                    if (userName.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                        return 3;
                    goto case 1;

                case 1:
                    if (userName.StartsWith(searchName, StringComparison.OrdinalIgnoreCase))
                        return 2;
                    goto case 0;

                case 0:
                    if (userName.IndexOf(searchName, StringComparison.OrdinalIgnoreCase) != -1)
                        return 1;
                    break;

                default:
                    goto case 2;
            }

            return -1;
        }

        public Task<IReadOnlyCollection<IUser>> GetUsersAsync(string userType)
        {
            return !SupportsUserType(userType)
                ? Task.FromResult((IReadOnlyCollection<IUser>)Enumerable.Empty<IUser>())
                : Task.FromResult<IReadOnlyCollection<IUser>>(DummyUsers);
        }

        public async Task BroadcastAsync(string message, Color? color = null)
        {
            var sColor = color ?? Color.White;

            foreach (var user in DummyUsers)
            {
                await user.PrintMessageAsync(message, sColor);
            }
        }

        public Task BroadcastAsync(string userType, string message, Color? color = null)
        {
            return !SupportsUserType(userType) ? Task.CompletedTask : BroadcastAsync(message, color);
        }

        public Task<bool> BanAsync(IUser user, string? reason = null, DateTime? endTime = null)
        {
            return BanAsync(user, null, reason, endTime);
        }

        public async Task<bool> BanAsync(IUser user, IUser? instigator = null, string? reason = null,
            DateTime? endTime = null)
        {
            if (user is not DummyUser dummy)
            {
                return false;
            }

            reason ??= "No reason provided";
            endTime ??= DateTime.MaxValue;
            if (!ulong.TryParse(instigator?.Id, out var instigatorId))
            {
                instigatorId = 0;
            }

            var banDuration = (uint)(endTime.Value - DateTime.Now).TotalSeconds;
            if (banDuration <= 0)
                return false;

            await UniTask.SwitchToMainThread();
            var ip = dummy.SteamPlayer.getIPv4AddressOrZero();
            return Provider.requestBanPlayer((CSteamID)instigatorId, dummy.SteamID, ip, dummy.SteamPlayer.playerID.GetHwids(), reason, banDuration);
        }

        public async Task<bool> KickAsync(IUser user, string? reason = null)
        {
            if (user is not DummyUser dummy)
            {
                return false;
            }

            await dummy.Session?.DisconnectAsync(reason!)!;
            return true;
        }

        public async Task<DummyUser> AddDummyAsync(CSteamID? id, HashSet<CSteamID>? owners = null,
            UnturnedUser? userCopy = null, ConfigurationSettings? settings = null)
        {
            var localizer = m_PluginAccessor.Instance?.LifetimeScope.Resolve<IStringLocalizer>() ??
                            NullStringLocalizer.Instance;
            var configuration = m_PluginAccessor.Instance?.Configuration ?? NullConfiguration.Instance;
            var config = configuration.Get<Configuration>();

            //id ??= GetAvailableId();
            owners ??= new();

            var sId = id ?? GetAvailableId();

            ValidateSpawn(sId);

            SteamPlayerID playerID;
            SteamPending pending;

            if (userCopy != null)
            {
                var userSteamPlayer = userCopy.Player.SteamPlayer;
                var uPlayerID = userCopy.Player.SteamPlayer.playerID;
                playerID = new(sId, uPlayerID.characterID, uPlayerID.playerName, uPlayerID.characterName,
                    uPlayerID.nickName, uPlayerID.group);

                pending = new(GetTransportConnection(), playerID, userSteamPlayer.isPro,
                    userSteamPlayer.face, userSteamPlayer.hair, userSteamPlayer.beard, userSteamPlayer.skin,
                    userSteamPlayer.color, userSteamPlayer.markerColor, userSteamPlayer.hand,
                    (ulong)userSteamPlayer.shirtItem, (ulong)userSteamPlayer.pantsItem, (ulong)userSteamPlayer.hatItem,
                    (ulong)userSteamPlayer.backpackItem, (ulong)userSteamPlayer.vestItem,
                    (ulong)userSteamPlayer.maskItem, (ulong)userSteamPlayer.glassesItem, Array.Empty<ulong>(),
                    userSteamPlayer.skillset, userSteamPlayer.language, userSteamPlayer.lobbyID, EClientPlatform.Windows)
                {
                    hasProof = true,
                    hasGroup = true,
                    hasAuthentication = true
                };
            }
            else
            {
                settings ??= config.Default;
                if (settings is null)
                {
                    throw new ArgumentNullException(nameof(settings));
                }

                var skins = settings.Skins ?? throw new ArgumentException(nameof(settings.Skins));
                // var options = config.Options ?? throw new ArgumentException(nameof(config.Options));

                var skinColor = settings.SkinColor?.ToColor() ?? UColor.white;
                var color = settings.Color?.ToColor() ?? UColor.white;
                var markerColor = settings.MarkerColor?.ToColor() ?? UColor.white;

                playerID = new(sId, settings.CharacterId, settings.PlayerName,
                    settings.CharacterName, settings.NickName, settings.SteamGroupId, settings.Hwid.GetBytes());

                pending = new(GetTransportConnection(), playerID, settings.IsPro, settings.FaceId,
                    settings.HairId, settings.BeardId, skinColor, color, markerColor,
                    settings.IsLeftHanded, skins.Shirt, skins.Pants, skins.Hat,
                    skins.Backpack, skins.Vest, skins.Mask, skins.Glasses, Array.Empty<ulong>(),
                    settings.PlayerSkillset, settings.Language, settings.LobbyId, EClientPlatform.Windows)
                {
                    hasAuthentication = true,
                    hasGroup = true,
                    hasProof = true
                };
            }

            PrepareInventoryDetails(pending, userCopy != null);

            await PreAddDummyAsync(pending, config.Events!);
            try
            {
                await UniTask.SwitchToMainThread();

                Provider.accept(playerID, pending!.assignedPro, pending.assignedAdmin, pending.face,
                    pending.hair, pending.beard, pending.skin, pending.color, pending.markerColor, pending.hand,
                    pending.shirtItem, pending.pantsItem, pending.hatItem, pending.backpackItem, pending.vestItem,
                    pending.maskItem, pending.glassesItem, pending.skinItems, pending.skinTags,
                    pending.skinDynamicProps, pending.skillset, pending.language, pending.lobbyID);

                var probDummyPlayer = Provider.clients.LastOrDefault();
                if (probDummyPlayer?.playerID.steamID != playerID.steamID)
                {
                    throw new DummyCanceledSpawnException(
                        $"Plugin or Game rejected connection of a dummy {pending.playerID.steamID}");
                }

                var options = config.Options ?? throw new ArgumentNullException(nameof(config.Options));
                var fun = config.Fun ?? throw new ArgumentNullException(nameof(config.Fun));

                var dummyUser = new DummyUser(UserProvider, m_UserDataStore, probDummyPlayer, m_LoggerFactory, localizer,
                    options.DisableSimulations, owners);

                PostAddDummy(dummyUser, options, fun);
                return dummyUser;
            }
            catch (DummyCanceledSpawnException)
            {
                Provider.pending.Remove(pending);
                throw;
            }
            catch
            {
                var i = Provider.clients.FindIndex(x => x.playerID == playerID);
                if (i >= 0)
                {
                    Provider.clients.RemoveAt(Provider.clients.FindIndex(x => x.playerID == playerID));
                }

                throw;
            }
        }

        private async UniTask PreAddDummyAsync(SteamPending pending, ConfigurationEvents events)
        {
            await UniTask.SwitchToMainThread();

            Provider.pending.Add(pending);

            if (events.CallOnCheckBanStatusWithHwid)
            {
                pending.transportConnection.TryGetIPv4Address(out var ip);
                var isBanned = false;
                var banReason = string.Empty;
                var banRemainingDuration = 0U;
                if (SteamBlacklist.checkBanned(pending.playerID.steamID, ip, pending.playerID.GetHwids(), out var steamBlacklistID))
                {
                    isBanned = true;
                    banReason = steamBlacklistID!.reason;
                    banRemainingDuration = steamBlacklistID.getTime();
                }

                try
                {
                    Provider.onCheckBanStatusWithHWID?.Invoke(pending.playerID, ip, ref isBanned, ref banReason,
                        ref banRemainingDuration);
                }
                catch (Exception e)
                {
                    m_Logger.LogError(e, "Plugin raised an exception from onCheckBanStatusWithHWID");
                }

                if (isBanned)
                {
                    Provider.pending.Remove(pending);
                    throw new DummyCanceledSpawnException(
                        $"Dummy {pending.playerID.steamID} is banned! Ban reason: {banReason}, duration: {banRemainingDuration}");
                }
            }

            if (events.CallOnCheckValidWithExplanation)
            {
                var isValid = true;
                var explanation = string.Empty;
                try
                {
                    Provider.onCheckValidWithExplanation(new()
                    {
                        m_SteamID = pending.playerID.steamID,
                        m_eAuthSessionResponse = EAuthSessionResponse.k_EAuthSessionResponseOK,
                        m_OwnerSteamID = pending.playerID.steamID
                    }, ref isValid, ref explanation);
                }
                catch (Exception e)
                {
                    m_Logger.LogError(e, "Plugin raised an exception from onCheckValidWithExplanation");
                }

                if (!isValid)
                {
                    Provider.pending.Remove(pending);
                    throw new DummyCanceledSpawnException(
                        $"Plugin reject connection of a dummy {pending.playerID.steamID}. Reason: {explanation}");
                }
            }
        }

        private void PrepareInventoryDetails(SteamPending pending, bool isPlayer)
        {
            pending.shirtItem = (int)pending.packageShirt;
            pending.pantsItem = (int)pending.packagePants;
            pending.hatItem = (int)pending.packageHat;
            pending.backpackItem = (int)pending.packageBackpack;
            pending.vestItem = (int)pending.packageVest;
            pending.maskItem = (int)pending.packageMask;
            pending.glassesItem = (int)pending.packageGlasses;
            pending.skinItems = new int[]
            {
                pending.shirtItem, pending.pantsItem, pending.hatItem, pending.backpackItem, pending.vestItem,
                pending.maskItem, pending.glassesItem
            };
            pending.skinTags = Array.Empty<string>();
            pending.skinDynamicProps = Array.Empty<string>();
        }

        private void PostAddDummy(DummyUser playerDummy, ConfigurationOptions options, ConfigurationFun fun)
        {
            if (fun.AlwaysRotate)
            {
                RotateDummyTask(playerDummy, fun.RotateYaw).Forget();
            }

            DummyUsers.Add(playerDummy);
        }

        private async UniTaskVoid RotateDummyTask(DummyUser player, float rotateYaw)
        {
            while (!m_Disposed && player.Simulation.Enabled)
            {
                await UniTask.Delay(1);
                player.Simulation.SetRotation(player.Player.Player.look.yaw + rotateYaw, player.Player.Player.look.pitch, 1f);
            }
        }

        [AssertionMethod]
        private void ValidateSpawn(CSteamID id)
        {
            var localizer = m_PluginAccessor.Instance?.LifetimeScope.Resolve<IStringLocalizer>() ??
                            NullStringLocalizer.Instance;
            var configuration = m_PluginAccessor.Instance?.Configuration ?? NullConfiguration.Instance;

            if (Provider.clients.Any(x => x.playerID.steamID == id))
            {
                throw new DummyContainsException(localizer, id.m_SteamID); // or id is taken
            }

            var amountDummiesConfig = configuration.Get<Configuration>().Options?.AmountDummies ?? 0;
            if (amountDummiesConfig != 0 && DummyUsers.Count + 1 > amountDummiesConfig)
            {
                throw new DummyOverflowsException(localizer, (byte)DummyUsers.Count, amountDummiesConfig);
            }
        }

        private async Task DontAutoKickTask()
        {
            var time = (int)Math.Floor(Provider.configData.Server.Timeout_Game_Seconds * 0.5f);

            while (!m_Disposed)
            {
                foreach (var dummy in DummyUsers)
                {
                    dummy.SteamPlayer.timeLastPacketWasReceivedFromClient = Time.realtimeSinceStartup;
                }

                await Task.Delay(time);
            }
        }

        public virtual CSteamID GetAvailableId()
        {
            var result = new CSteamID(1);

            while (DummyUsers.Any(x => x.SteamID == result))
            {
                result.m_SteamID++;
            }

            return result;
        }

        public virtual ITransportConnection GetTransportConnection()
        {
            return m_LifetimeScope.Resolve<ITransportConnection>();
        }

        public async ValueTask DisposeAsync()
        {
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            Provider.onCommenceShutdown -= ProviderOnonCommenceShutdown;
            Provider.onServerDisconnected -= OnServerDisconnected;

            if (!m_IsShuttingDown)
            {
                foreach (var user in DummyUsers)
                {
                    await user.DisposeAsync();
                }
            }
        }
    }
}