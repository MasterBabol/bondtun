using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Bondtun
{
    public class BondClient : IInstance
    {
        private List<KeyValuePair<TcpClient, Stream>> m_netLinks = new List<KeyValuePair<TcpClient, Stream>>();
        private List<KeyValuePair<IPEndPoint, IPEndPoint>> m_linkInf = new List<KeyValuePair<IPEndPoint, IPEndPoint>>();
        private TcpListener m_listener;
        private Int32 m_maxConns;
        private TcpClient m_serveClient;
        private NetworkStream m_serveStream;
        private Int32 m_bufferSize;
        private BlockingCollection<Byte[]> m_serveOutboundQueue;

        public BondClient(XmlElement fromXml, Int32 bufferSize)
        {
            XmlElement serve = (XmlElement)fromXml.GetElementsByTagName("bind").Item(0);

            IPAddress ip;
            if (!IPAddress.TryParse(serve.GetAttribute("ip"), out ip))
                ip = new IPAddress(0);
            Int32 port = Int32.Parse(serve.GetAttribute("port"));

            IPEndPoint newEP = new IPEndPoint(ip, port);
            m_listener = new TcpListener(newEP);
            m_listener.Start(1);

            m_maxConns = 0;
            foreach (XmlElement link in fromXml.GetElementsByTagName("link"))
            {
                IPAddress localip;
                if (!IPAddress.TryParse(link.GetAttribute("localip"), out localip))
                    ip = new IPAddress(0);
                Int32 localport = Int32.Parse(link.GetAttribute("localport"));

                IPEndPoint newLocalEP = new IPEndPoint(localip, localport);

                IPAddress remoteip = Dns.GetHostAddresses(link.GetAttribute("remotehost"))[0];
                Int32 remoteport = Int32.Parse(link.GetAttribute("remoteport"));

                IPEndPoint newRemoteEP = new IPEndPoint(remoteip, remoteport);

                m_linkInf.Add(new KeyValuePair<IPEndPoint, IPEndPoint>(newLocalEP, newRemoteEP));
                m_maxConns++;
            }

            m_bufferSize = bufferSize;

            m_serveOutboundQueue = new BlockingCollection<byte[]>(m_maxConns);
        }

        public void RunSync()
        {
            RunAsync().Wait();
        }

        public async Task RunAsync()
        {
            try
            {
                m_serveClient = await m_listener.AcceptTcpClientAsync();
                m_serveClient.SendBufferSize = m_bufferSize;
                m_serveClient.ReceiveBufferSize = m_bufferSize;
                m_serveStream = m_serveClient.GetStream();

                foreach (var inf in m_linkInf)
                {
                    TcpClient newClient = new TcpClient(inf.Key);
		    Console.WriteLine("A link from " + inf.Key + " is connecting to " + inf.Value);
                    await newClient.ConnectAsync(inf.Value.Address, inf.Value.Port);
                    newClient.SendBufferSize = m_bufferSize;
                    newClient.ReceiveBufferSize = m_bufferSize;

                    m_netLinks.Add(new KeyValuePair<TcpClient, Stream>(newClient, newClient.GetStream()));
                }

                var difl = Task.Run(async () => { await DispatchInboundFromLink(); });
                var dotlr = Task.Run(async () => { await DispatchOutboundToLinkRead(); });
                var dotlw = Task.Run(async () => { await DispatchOutboundToLinkWrite(); });
                await Task.WhenAll(difl, dotlr, dotlw);
            }
            catch (Exception)
            {
                DisposeAll();
            }
        }

        private async Task DispatchInboundFromLink()
        {
            try
            {
                while (m_serveClient.Connected)
                {
                    foreach (var link in m_netLinks)
                    {
                        Byte[] payload = await ReceiveOne(link.Value);
                        await m_serveStream.WriteAsync(payload);
                    }
                }
            }
            catch (Exception)
            {
                DisposeAll();
            }
        }

        private async Task DispatchOutboundToLinkRead()
        {
            try
            {
                while (m_serveClient.Connected)
                {
                    Byte[] buffer = new Byte[1500 / m_maxConns];
                    Int32 readBytes = await m_serveStream.ReadAsync(buffer, 4, buffer.Length - 4);

                    if (readBytes > 0)
                    {
                        Array.Copy(BitConverter.GetBytes(readBytes), buffer, 4);
                        Array.Resize(ref buffer, readBytes + 4);
                        while (!m_serveOutboundQueue.TryAdd(buffer))
                            await Task.Yield();
                    }
                    else
                        throw new SocketException();
                }
            }
            catch (Exception)
            {
                DisposeAll();
            }
        }

        private async Task DispatchOutboundToLinkWrite()
        {
            try
            {
                Queue<KeyValuePair<TcpClient, Stream>> schedulingTargets = new Queue<KeyValuePair<TcpClient, Stream>>();

                while (m_serveClient.Connected)
                {
                    List<Task> writeTasks = new List<Task>();

                    if (schedulingTargets.Count < m_maxConns)
                        foreach (var link in m_netLinks)
                            schedulingTargets.Enqueue(link);

                    for (int i = 0; i < m_maxConns; i++)
                    {
                        Byte[] buffer;
                        var link = schedulingTargets.Dequeue();

                        if (m_serveOutboundQueue.TryTake(out buffer))
                            writeTasks.Add(link.Value.WriteAsync(buffer, 0, buffer.Length));
                        else
                            await Task.Yield();
                    }

                    await Task.WhenAll(writeTasks.ToArray());
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
            m_serveClient.Dispose();
            m_serveStream.Dispose();
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
            if (len > 1024*1024*16)
                throw new OutOfMemoryException();

            Byte[] payload = new Byte[len];
            await ReceiveExact(stream, payload);

            return payload;
        }
    }
}
