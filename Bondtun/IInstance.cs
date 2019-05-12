using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Bondtun
{
    interface IInstance
    {
        void RunSync();
        Task RunAsync();
    }
}
