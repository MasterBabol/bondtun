﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Bondtun
{
    public class BondServer : IInstance
    {
        private Dictionary<TcpClient, Stream> m_netLinks = new Dictionary<TcpClient, Stream>();
        private TcpListener m_listener;
        private Int32 m_maxConns;
        private TcpClient m_remoteClient;
        private IPEndPoint m_remoteEP;
        private NetworkStream m_remoteStream;
        private Int32 m_bufferSize;

        public BondServer(XmlElement fromXml, Int32 bufferSize)
        {
            XmlElement local = (XmlElement)fromXml.GetElementsByTagName("local").Item(0);

            IPAddress ip;
            if (!IPAddress.TryParse(local.GetAttribute("ip"), out ip))
                ip = new IPAddress(0);
            Int32 port = Int32.Parse(local.GetAttribute("port"));

            m_maxConns = Int32.Parse(local.GetAttribute("conns"));

            IPEndPoint newEP = new IPEndPoint(ip, port);
            m_listener = new TcpListener(newEP);
            m_listener.Start(m_maxConns);

            XmlElement remote = (XmlElement)fromXml.GetElementsByTagName("remote").Item(0);
            IPAddress remoteIp = Dns.GetHostAddresses(remote.GetAttribute("host"))[0];
            Int32 remotePort = Int32.Parse(remote.GetAttribute("port"));
            m_remoteEP = new IPEndPoint(remoteIp, remotePort);

            m_bufferSize = bufferSize;
        }

        public void RunSync()
        {
            RunAsync().Wait();
        }

        public async Task RunAsync()
        {
            try
            {
                for (int i = 0; i < m_maxConns; i++)
                {
                    TcpClient newClient;
                    newClient = await m_listener.AcceptTcpClientAsync();
                    newClient.SendBufferSize = m_bufferSize;
                    newClient.ReceiveBufferSize = m_bufferSize;
                    m_netLinks.Add(newClient, newClient.GetStream());
                }

                m_remoteClient = new TcpClient();
                m_remoteClient.SendBufferSize = m_bufferSize;
                m_remoteClient.ReceiveBufferSize = m_bufferSize;
                await m_remoteClient.ConnectAsync(m_remoteEP.Address, m_remoteEP.Port);
                m_remoteStream = m_remoteClient.GetStream();

                var difr = Task.Run(async () => { await DispatchInboundFromRemote(); });
                var dotr = Task.Run(async () => { await DispatchOutboundToRemote(); });
                await Task.WhenAll(difr, dotr);
            }
            catch (Exception)
            {
                foreach (var link in m_netLinks)
                    link.Key.Dispose();
            }
        }

        private async Task DispatchInboundFromRemote()
        {
            try
            {
                while (m_remoteClient.Connected)
                {
                    foreach (var link in m_netLinks)
                    {
                        Byte[] buffer = new Byte[65536];
                        Int32 readBytes = await m_remoteStream.ReadAsync(buffer);

                        if (readBytes > 0)
                        {
                            await link.Value.WriteAsync(BitConverter.GetBytes(readBytes));
                            await link.Value.WriteAsync(buffer, 0, (Int32)readBytes);
                        }
                        else
                            throw new SocketException();
                    }
                }
            }
            catch (Exception)
            {
                DisposeAll();
            }
        }

        private async Task DispatchOutboundToRemote()
        {
            try
            {
                while (m_remoteClient.Connected)
                {
                    foreach (var link in m_netLinks)
                    {
                        Byte[] payload = await ReceiveOne(link.Value);
                        await m_remoteStream.WriteAsync(payload);
                    }
                }
            }
            catch (Exception)
            {
                DisposeAll();
            }
        }

        private void DisposeAll()
        {
            foreach (var link in m_netLinks)
                link.Key.Dispose();
            m_remoteClient.Dispose();
            m_remoteStream.Dispose();
        }

        private async Task ReceiveExact(Stream stream, Byte[] buffer)
        {
            Int32 offset = 0;
            Int32 readBytes = 0;

            while (buffer.Length - offset > 0)
            {
                readBytes = await stream.ReadAsync(buffer, offset, buffer.Length - offset);
                if (readBytes > 0)
                    offset += readBytes;
                else
                    throw new IOException();
            }
        }

        private async Task<Byte[]> ReceiveOne(Stream stream)
        {
            Byte[] lengthRaw = new Byte[4];

            await ReceiveExact(stream, lengthRaw);
            UInt32 len = BitConverter.ToUInt32(lengthRaw);
            if (len > 65536)
                throw new OutOfMemoryException();

            Byte[] payload = new Byte[len];
            await ReceiveExact(stream, payload);

            return payload;
        }
    }
}
