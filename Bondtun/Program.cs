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
            if (args.Length >= 1)
                configFileName = args[0];

            try
            {
                XmlReaderSettings readerSettings = new XmlReaderSettings();
                readerSettings.DtdProcessing = DtdProcessing.Parse;
                var reader = XmlReader.Create(configFileName, readerSettings);
                reader.MoveToContent();

                var configXml = new XmlDocument();
                configXml.Load(reader);

                List<IInstance> insts = new List<IInstance>();
                var configRoot = configXml.FirstChild;
                if (configRoot.Name == "insts")
                {
                    foreach (XmlElement inst in configRoot.ChildNodes)
                    {
                        if (inst.Name == "server")
                        {
                            var server = new BondServer(inst);
                            insts.Add(server);
                        }
                        else if (inst.Name == "client")
                        {
                            var client = new BondClient(inst);
                            insts.Add(client);
                        }
                        else
                            throw new ArgumentException(String.Format("Unexpected instance role '{0}'.", inst.Name));
                    }

                    List<Task> instTasks = new List<Task>();
                    foreach (var inst in insts)
                        instTasks.Add(inst.RunAsync());

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
