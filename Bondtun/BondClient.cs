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
        private TcpClient m_serveClient;
        private NetworkStream m_serveStream;

        public BondClient(XmlElement fromXml)
        {
            XmlElement serve = (XmlElement)fromXml.GetElementsByTagName("bind").Item(0);

            IPAddress ip;
            if (!IPAddress.TryParse(serve.GetAttribute("ip"), out ip))
                ip = new IPAddress(0);
            Int32 port = Int32.Parse(serve.GetAttribute("port"));

            IPEndPoint newEP = new IPEndPoint(ip, port);
            m_listener = new TcpListener(newEP);
            m_listener.Start(1);

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
            }
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
                m_serveClient.SendBufferSize = 65536;
                m_serveClient.ReceiveBufferSize = 65536;
                m_serveStream = m_serveClient.GetStream();

                foreach (var inf in m_linkInf)
                {
                    TcpClient newClient = new TcpClient(inf.Key);
                    await newClient.ConnectAsync(inf.Value.Address, inf.Value.Port);
                    newClient.SendBufferSize = 65536;
                    newClient.ReceiveBufferSize = 65536;

                    m_netLinks.Add(newClient, newClient.GetStream());
                }

                while (m_serveClient.Connected)
                {
                    foreach (var link in m_netLinks)
                    {
                        Byte[] buffer = new Byte[1400];
                        Int32 readBytes = await m_serveStream.ReadAsync(buffer);

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
                foreach (var link in m_netLinks)
                    link.Key.Dispose();
            }
        }
    }
}
