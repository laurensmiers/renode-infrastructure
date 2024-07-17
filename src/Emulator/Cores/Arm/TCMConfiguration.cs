//
// Copyright (c) 2010-2024 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Linq;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;

namespace Antmicro.Renode.Peripherals.CPU
{
    public class TCMConfiguration
    {
        public TCMConfiguration(uint address, ulong size, uint regionIndex, uint interfaceIndex = 0)
        {
            Address = address;
            Size = size;
            InterfaceIndex = interfaceIndex;
            RegionIndex = regionIndex;
        }

        public static bool TryFindRegistrationAddress(IBusController sysbus, ICPU cpu, MappedMemory memory, out ulong address)
    	{
            address = 0x0ul;
            var registrationPoint = ((SystemBus)sysbus).GetRegistrationPoints(memory, cpu).OfType<IBusRegistration>().Where(x => x.CPU == cpu).SingleOrDefault();
            if(registrationPoint == null)
            {
                return false;
            }
            if(registrationPoint is BusRangeRegistration rangeRegistration)
            {
                address = rangeRegistration.Range.StartAddress;
            }
            else if(registrationPoint is BusPointRegistration pointRegistration)
            {
                address = pointRegistration.StartingPoint;
            }
            else
            {
                return false;
            }
            return true;
    	}

        public uint Address { get; }
        public ulong Size { get; }
        public uint InterfaceIndex { get; }
        public uint RegionIndex { get; }
    }
}
