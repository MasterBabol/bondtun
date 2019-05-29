using System;
using System.Collections.Generic;
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
        private Dictionary<TcpClient, Stream> m_netLinks = new Dictionary<TcpClient, Stream>();
        private List<KeyValuePair<IPEndPoint, IPEndPoint>> m_linkInf = new List<KeyValuePair<IPEndPoint, IPEndPoint>>();
        private TcpListener m_listener;
        private Int32 m_maxConns;
        private TcpClient m_serveClient;
        private NetworkStream m_serveStream;
        private Int32 m_bufferSize;

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
                Console.WriteLine("A link from the local bind from " + m_serveClient.Client.RemoteEndPoint.ToString() + " is connected");
                m_serveClient.SendBufferSize = m_bufferSize;
                m_serveClient.ReceiveBufferSize = m_bufferSize;
                m_serveStream = m_serveClient.GetStream();

                foreach (var inf in m_linkInf)
                {
                    TcpClient newClient = new TcpClient(inf.Key);
                    Console.WriteLine("A link from " + inf.Key + " is connecting to " + inf.Value);
                    await newClient.ConnectAsync(inf.Value.Address, inf.Value.Port);
                    Console.WriteLine("A link from " + inf.Key + " is connected ");
                    newClient.SendBufferSize = m_bufferSize;
                    newClient.ReceiveBufferSize = m_bufferSize;

                    m_netLinks.Add(newClient, newClient.GetStream());
                }
                Console.WriteLine("All links are now ready");

                var difl = Task.Run(() => { DispatchInboundFromLink(); });
                var dotl = Task.Run(() => { DispatchOutboundToLink(); });
                await Task.WhenAll(difl, dotl);
            }
            catch (Exception e)
            {
                DisposeAll();
                Console.WriteLine(e);
            }
        }

        private void DispatchInboundFromLink()
        {
            try
            {
                while (m_serveClient.Connected)
                {
                    foreach (var link in m_netLinks)
                    {
                        Byte[] payload = ReceiveOne(link.Value);
                        m_serveStream.Write(payload);
                    }
                }
            }
            catch (Exception e)
            {
                DisposeAll();
                Console.WriteLine(e);
            }
        }

        private void DispatchOutboundToLink()
        {
            try
            {
                while (m_serveClient.Connected)
                {
                    foreach (var link in m_netLinks)
                    {
                        Byte[] buffer = new Byte[1500 / m_maxConns];
                        Int32 readBytes = m_serveStream.Read(buffer, 4, buffer.Length - 4);

                        if (readBytes > 0)
                        {
                            Array.Copy(BitConverter.GetBytes(readBytes), buffer, 4);
                            link.Value.Write(buffer, 0, (Int32)readBytes + 4);
                        }
                        else
                            throw new IOException("readBytes has returned " + readBytes);
                    }
                }
            }
            catch (Exception e)
            {
                DisposeAll();
                Console.WriteLine(e);
            }
        }

        private void DisposeAll()
        {
            foreach (var link in m_netLinks)
                link.Key.Dispose();
            m_serveClient.Dispose();
            m_serveStream.Dispose();
        }

        private void ReceiveExact(Stream stream, Byte[] buffer)
        {
            Int32 offset = 0;
            Int32 readBytes = 0;

            while (buffer.Length - offset > 0)
            {
                readBytes = stream.Read(buffer, offset, buffer.Length - offset);
                if (readBytes > 0)
                    offset += readBytes;
                else
                    throw new IOException("readBytes has returned " + readBytes);
            }
        }

        private Byte[] ReceiveOne(Stream stream)
        {
            Byte[] lengthRaw = new Byte[4];

            ReceiveExact(stream, lengthRaw);
            UInt32 len = BitConverter.ToUInt32(lengthRaw);
            if (len > 1024*1024*16)
                throw new OutOfMemoryException();

            Byte[] payload = new Byte[len];
            ReceiveExact(stream, payload);

            return payload;
        }
    }
}
