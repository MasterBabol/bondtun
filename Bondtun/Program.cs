using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Bondtun
{
    class Program
    {
        static void Main(string[] args)
        {
#if DEBUG
            String configFileName = "config-dbg.xml";
#else
            String configFileName = "config.xml";
#endif
            Int32 bufferSize = 65536;

            if (args.Length >= 1)
                configFileName = args[0];

            if (args.Length >= 2)
                Int32.TryParse(args[1], out bufferSize);

            try
            {
                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.DtdProcessing = DtdProcessing.Parse;
                var reader = XmlReader.Create(configFileName, readerSettings);
                reader.MoveToContent();

                var configXml = new XmlDocument();
                configXml.Load(reader);

                List<IInstance> insts = new List<IInstance>();
                var configRoot = (XmlElement)configXml.FirstChild;
                if (configRoot.Name == "insts")
                {
                    foreach (XmlElement serverInst in configRoot.GetElementsByTagName("server"))
                    {
                        var server = new BondServer(serverInst, bufferSize);
                        insts.Add(server);
                    }

                    foreach (XmlElement clientInst in configRoot.GetElementsByTagName("client"))
                    {
                        var client = new BondClient(clientInst, bufferSize);
                        insts.Add(client);
                    }

                    List<Task> instTasks = new List<Task>();
                    foreach (var inst in insts)
                        instTasks.Add(Task.Run(async () => { await inst.RunAsync(); }));

                    Task.WaitAll(instTasks.ToArray());
                }
                else
                    throw new ArgumentException(String.Format("No instance config is found."));
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }
}
