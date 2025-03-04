﻿using System;
using System.Net;
using Microsoft.Extensions.Configuration;
using OpenMod.API.Plugins;
using SDG.NetTransport;
using SDG.Unturned;

namespace Dummy.NetTransports
{
    public class DummyTransportConnection : ITransportConnection
    {
        private readonly string m_IP;
        private readonly ushort m_Port;
        private readonly IPAddress m_Address;

        public DummyTransportConnection(IPluginAccessor<Dummy> pluginAccessor)
        {
            var random = new Random();
            var configuration = pluginAccessor.Instance!.Configuration;

            var randomizeIp = configuration.GetValue("connection:randomIp", true);
            var randomizePort = configuration.GetValue("connection:randomPort", true);
            m_IP = randomizeIp
                ? $"{random.Next(1, 256)}.{random.Next(256)}.{random.Next(256)}.{random.Next(256)}"
                : configuration["default:ip"];
            m_Port = randomizePort
                ? (ushort)random.Next(IPEndPoint.MinPort + 1, IPEndPoint.MaxPort + 1)
                : configuration.GetSection("default:port").Get<ushort>();
            m_Address = IPAddress.Parse(m_IP);
        }

        public void CloseConnection()
        {
        }

        public bool Equals(ITransportConnection other)
        {
            return Equals(this, other);
        }

        public bool TryGetIPv4Address(out uint address)
        {
            address = Parser.getUInt32FromIP(m_IP);
            return true;
        }

        public bool TryGetPort(out ushort port)
        {
            port = m_Port;
            return true;
        }

        public IPAddress GetAddress()
        {
            return m_Address;
        }

        public string GetAddressString(bool withPort)
        {
            return m_IP + (withPort ? ":" + m_Port : string.Empty);
        }

        public void Send(byte[] buffer, long size, ENetReliability sendType)
        {
            var invokableReader = NetMessages.GetInvokableReader();
            invokableReader.SetBufferSegmentCopy(buffer, Provider.buffer, (int)size);
            invokableReader.Reset();
            invokableReader.ReadEnum(out EClientMessage eclientMessage);
            if (eclientMessage is not EClientMessage.InvokeMethod)
            {
                return;
            }

            invokableReader.ReadBits(NetReflection.clientMethodsBitCount, out var num);
            if (num >= NetReflection.clientMethodsLength)
                return;

            var method = NetReflection.clientMethods[num];

            if (method.declaringType == typeof(PlayerInput)
                && method.debugName.Contains("ReceiveSimulateMispredictedInputs"))
            {
                var cic = new ClientInvocationContext(ClientInvocationContext.EOrigin.Remote, invokableReader, method);
                method.readMethod(in cic);
            }
        }
    }
}