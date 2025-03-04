﻿extern alias JetBrainsAnnotations;
using Cysharp.Threading.Tasks;
using Dummy.API;
using OpenMod.API.Eventing;
using OpenMod.Core.Eventing;
using OpenMod.UnityEngine.Extensions;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Players.Life.Events;
using OpenMod.Unturned.Users;
using SDG.Unturned;
using System.Threading.Tasks;
using JetBrainsAnnotations::JetBrains.Annotations;
using SDG.NetTransport;
using UnityEngine;
using Microsoft.Extensions.Configuration;

namespace Dummy.Events
{
    [UsedImplicitly]
    public class DummyDeadEvent : IEventListener<UnturnedPlayerDeathEvent>
    {
        private static readonly ClientInstanceMethod<Vector3, byte> s_SendRevive =
            ClientInstanceMethod<Vector3, byte>.Get(typeof(PlayerLife), "ReceiveRevive");

        private readonly IDummyProvider m_DummyProvider;
        private readonly IUnturnedUserDirectory m_UnturnedUserDirectory;
        private readonly IConfiguration m_Configuration;

        public DummyDeadEvent(IDummyProvider dummyProvider, IUnturnedUserDirectory unturnedUserDirectory, IConfiguration configuration)
        {
            m_DummyProvider = dummyProvider;
            m_UnturnedUserDirectory = unturnedUserDirectory;
            m_Configuration = configuration;
        }

        [EventListener(Priority = EventListenerPriority.Monitor)]
        public async Task HandleEventAsync(object? sender, UnturnedPlayerDeathEvent @event)
        {
            if (!m_Configuration.GetValue<bool>("logs:enableDeathLog"))
            {
                return;
            }

            var dummy = await m_DummyProvider.FindDummyUserAsync(@event.Player.SteamId.m_SteamID);
            if (dummy == null)
            {
                return;
            }

            foreach (var owner in dummy.Owners)
            {
                var player = m_UnturnedUserDirectory.FindUser(owner);
                if (player == null)
                {
                    continue;
                }

                await player.PrintMessageAsync(
                    $"Dummy {@event.Player.SteamId} has died. Death reason: {@event.DeathCause.ToString().ToLower()}, killer = {@event.Instigator}. Respawning...");
            }

            Revive(dummy.Player).Forget();
        }

        private static async UniTaskVoid Revive(UnturnedPlayer player)
        {
            await UniTask.Delay(1500);
            if (player.Player == null || player.IsAlive)
            {
                return;
            }

            var life = player.Player.life;
            var transform = life.transform;
            life.sendRevive();

            s_SendRevive.InvokeAndLoopback(life.GetNetId(), ENetReliability.Reliable,
                Provider.EnumerateClients_Remote(), transform.position,
                MeasurementTool.angleToByte(transform.rotation.eulerAngles.y));
        }
    }
}