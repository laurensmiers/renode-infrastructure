//
// Copyright (c) 2010-2024 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Extensions;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    [AllowedTranslations(AllowedTranslation.QuadWordToDoubleWord | AllowedTranslation.DoubleWordToByte | AllowedTranslation.WordToByte)]
    public class CoreLocalInterruptController : BasicBytePeripheral, IDoubleWordPeripheral, IIndirectCSRPeripheral, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IGPIOReceiver
    {
        public CoreLocalInterruptController(IMachine machine, BaseRiscV cpu, uint numberOfInterrupts = 4096, uint numberOfTriggers = 32,
            ulong machineLevelBits = 8, // MNLBITS
            ulong supervisorLevelBits = 8, // SNLBITS
            ulong modeBits = 2, // NMBITS
            // add the nvbits field to the configuration register to match the legacy layout from the 2022-09-27 specification,
            // as found in some hardware implementations
            bool configurationHasNvbits = true
            )
            : base(machine)
        {
            if(machineLevelBits > 8)
            {
                throw new ConstructionException($"Invalid machineLevelBits: provided {machineLevelBits} is larger than the maximum 8.");
            }

            if(supervisorLevelBits > 8)
            {
                throw new ConstructionException($"Invalid supervisorLevelBits: provided {supervisorLevelBits} is larger than the maximum 8.");
            }

            if(modeBits > 2)
            {
                throw new ConstructionException($"Invalid modeBits: provided {modeBits} is larger than the maximum 2.");
            }

            if(numberOfInterrupts < 2 || numberOfInterrupts > 4096)
            {
                throw new ConstructionException($"Invalid numberOfInterrupts: provided {numberOfInterrupts} but must be between 2 and 4096.");
            }

            this.cpu = cpu;
            this.numberOfInterrupts = numberOfInterrupts;
            this.numberOfTriggers = numberOfTriggers;
            defaultMachineLevelBits = machineLevelBits;
            defaultSupervisorLevelBits = supervisorLevelBits;
            defaultModeBits = modeBits;
            this.configurationHasNvbits = configurationHasNvbits;

            DoubleWordRegisters = new DoubleWordRegisterCollection(this);

            interruptPending = new IFlagRegisterField[numberOfInterrupts];
            interruptEnable = new IFlagRegisterField[numberOfInterrupts];
            vectored = new IFlagRegisterField[numberOfInterrupts];
            edgeTriggered = new IFlagRegisterField[numberOfInterrupts];
            negative = new IFlagRegisterField[numberOfInterrupts];
            mode = new IValueRegisterField[numberOfInterrupts];
            inputControl = new IValueRegisterField[numberOfInterrupts];

            cpu.RegisterLocalInterruptController(this);

            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            machineLevelBits.Value = defaultMachineLevelBits;
            supervisorLevelBits.Value = defaultSupervisorLevelBits;
            modeBits.Value = defaultModeBits;
            bestInterrupt = NoInterrupt;
            acknowledgedInterrupt = NoInterrupt;
            cpu.ClicPresentInterrupt(NoInterrupt, false, MinLevel, PrivilegeLevel.User);
        }

        public uint ReadDoubleWord(long offset)
        {
            if(DoubleWordRegisters.TryRead(offset, out var result))
            {
                return result;
            }
            return this.ReadDoubleWordUsingByte(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(DoubleWordRegisters.TryWrite(offset, value))
            {
                return;
            }
            this.WriteDoubleWordUsingByte(offset, value);
        }

        public uint ReadIndirectCSR(uint iselect, uint ireg)
        {
            // iselect is the offset from the beginning of this peripheral's indirect CSR range
            // ireg is the 0-based index of the iregX CSR (ireg - 0, ireg2 - 1, ...)
            if(iselect < InterruptControlAttribute || iselect > ClicConfiguration || (ireg != 0 && ireg != 1))
            {
                LogUnhandledIndirectCSRRead(iselect, ireg);
                return 0x0;
            }

            if(iselect < InterruptPendingEnable)
            {
                var start = (iselect - InterruptControlAttribute) * 4 + (ireg == 0 ? 2 : 3); // ireg: control, ireg 2: attr
                return ReadByte(start)
                    | ((uint)ReadByte(start + 4) << 8)
                    | ((uint)ReadByte(start + 8) << 16)
                    | ((uint)ReadByte(start + 12) << 24);
            }

            if(iselect < InterruptTrigger)
            {
                var start = (iselect - InterruptPendingEnable) * 32;
                return BitHelper.GetValueFromBitsArray(
                    ((ireg == 0) ? interruptPending : interruptEnable)
                        .Skip((int)start)
                        .Take(32)
                        .Select(r => r.Value)
                );
            }

            if(ireg == 1)
            {
                LogUnhandledIndirectCSRRead(iselect, ireg);
                return 0x0;
            }

            if(iselect < ClicConfiguration)
            {
                return ReadDoubleWord(iselect - InterruptTrigger + (long)Register.InterruptTrigger0);
            }

            this.WarningLog("Register reserved (iselect 0x{0:x}, ireg {1})", iselect, ireg);
            return 0x0;
        }

        public void WriteIndirectCSR(uint iselect, uint ireg, uint value)
        {
            // iselect is the offset from the beginning of this peripheral's indirect CSR range
            // ireg is the 0-based index of the iregX CSR (ireg - 0, ireg2 - 1, ...)
            if(iselect < InterruptControlAttribute || iselect > ClicConfiguration || (ireg != 0 && ireg != 1))
            {
                LogUnhandledIndirectCSRWrite(iselect, ireg);
                return;
            }

            if(iselect < InterruptPendingEnable)
            {
                var start = (iselect - InterruptControlAttribute) * 4 + (ireg == 0 ? 2 : 3);
                WriteByte(start, (byte)value);
                WriteByte(start + 4, (byte)(value >> 8));
                WriteByte(start + 8, (byte)(value >> 16));
                WriteByte(start + 16, (byte)(value >> 24));
                return;
            }

            if(iselect < InterruptTrigger)
            {
                var flags = (ireg == 0) ? interruptPending : interruptEnable;
                var i = (iselect - InterruptPendingEnable) * 32;
                foreach(var b in BitHelper.GetBits(value))
                {
                    flags[i++].Value = b;
                    // clicinttrig not implemented
                }
                return;
            }

            if(ireg == 1)
            {
                LogUnhandledIndirectCSRWrite(iselect, ireg);
                return;
            }

            if(iselect < ClicConfiguration)
            {
                WriteDoubleWord(iselect - InterruptTrigger + (long)Register.InterruptTrigger0, value);
                return;
            }

            this.WarningLog("Register reserved (iselect 0x{0:x}, ireg {1}), value 0x{2:X}", iselect, ireg, value);
            return;
        }

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number > numberOfInterrupts)
            {
                this.ErrorLog("Invalid GPIO number {0}: supported range is 0 to {1}", number, numberOfInterrupts);
                return;
            }

            if(edgeTriggered[number].Value)
            {
                interruptPending[number].Value |= value ^ negative[number].Value;
            }
            else
            {
                interruptPending[number].Value = value ^ negative[number].Value;
            }
            bool output = UpdateInterrupt();
            this.DebugLog("Incoming interrupt #{0} set to {1}, enabled={2} edgeTriggered={3} negative={4} -> output={5}",
                number, value, interruptEnable[number].Value, edgeTriggered[number].Value, negative[number].Value, output);
        }

        public void ClearEdgeInterrupt()
        {
            if(bestInterrupt == NoInterrupt || !edgeTriggered[bestInterrupt].Value)
            {
                return;
            }
            interruptPending[bestInterrupt].Value = false;
            UpdateInterrupt();
        }

        public void AcknowledgeInterrupt()
        {
            acknowledgedInterrupt = bestInterrupt;
        }

        private PrivilegeLevel GetInterruptPrivilege(int number)
        {
            if(number == NoInterrupt)
            {
                return PrivilegeLevel.User;
            }

            var itMode = mode[number].Value;
            switch(modeBits.Value)
            {
                case 0:
                    return PrivilegeLevel.Machine;
                case 1:
                    return (itMode & 0b10) == 0 ? PrivilegeLevel.Supervisor : PrivilegeLevel.Machine;
                case 2:
                    return (PrivilegeLevel)itMode; // matching representation
                default: // the reserved value 3 will be remapped on write, so this is unreachable
                    throw new InvalidOperationException($"Encountered reserved interrupt privilege {modeBits.Value}, should not happen");
            }
        }

        private int GetInterruptLevel(int number)
        {
            if(number == NoInterrupt)
            {
                return MinLevel; // MinLevel - normal execution, not in ISR
            }

            var privilege = GetInterruptPrivilege(number);
            var levelBits = (int)(privilege == PrivilegeLevel.Machine ? machineLevelBits.Value : supervisorLevelBits.Value);
            var priorityBits = InterruptInputControlBits - levelBits;
            // left-justify and append 1s to fill the unused bits on the right
            return (int)(BitHelper.GetValue(inputControl[number].Value, priorityBits, levelBits) << priorityBits) | ((1 << priorityBits) - 1);
        }

        private int GetInterruptPriority(int number)
        {
            if(number == NoInterrupt)
            {
                return -1; // below lowest valid priority
            }

            var privilege = GetInterruptPrivilege(number);
            var levelBits = (int)(privilege == PrivilegeLevel.Machine ? machineLevelBits.Value : supervisorLevelBits.Value);
            var priorityBits = InterruptInputControlBits - levelBits;
            return (int)(BitHelper.GetValue(inputControl[number].Value, 0, priorityBits) << levelBits) | ((1 << levelBits) - 1);
        }

        private bool UpdateInterrupt()
        {
            // Clear the previous best interrupt if it is not enabled or pending anymore
            if(bestInterrupt != NoInterrupt)
            {
                if(!(interruptEnable[bestInterrupt].Value && interruptPending[bestInterrupt].Value))
                {
                    bestInterrupt = NoInterrupt;
                }
            }
            var bestPrivilege = GetInterruptPrivilege(bestInterrupt);
            var bestLevel = GetInterruptLevel(bestInterrupt);
            var bestPriority = GetInterruptPriority(bestInterrupt);
            for(int i = 0; i < numberOfInterrupts; ++i)
            {
                if(!interruptEnable[i].Value || !interruptPending[i].Value)
                {
                    continue;
                }
                var currentPrivilege = GetInterruptPrivilege(i);
                var currentLevel = GetInterruptLevel(i);
                var currentPriority = GetInterruptPriority(i);
                // If the privilege or level is higher, take it as the best interrupt. If it only differs in priority, only take it if the core hasn't
                // already started handling the previous best interrupt, as priority does not cause preemption.
                if(currentPrivilege > bestPrivilege ||
                   currentLevel > bestLevel ||
                   (currentPrivilege == bestPrivilege && currentLevel == bestLevel && currentPriority > bestPriority && acknowledgedInterrupt == NoInterrupt))
                {
                    bestInterrupt = i;
                    bestLevel = currentLevel;
                    bestPriority = currentPriority;
                    bestPrivilege = currentPrivilege;
                }
            }
            if(bestInterrupt != NoInterrupt)
            {
                var bestVectored = vectored[bestInterrupt].Value;
                cpu.ClicPresentInterrupt(bestInterrupt, bestVectored, bestLevel, bestPrivilege);
                this.DebugLog("Presenting interrupt #{0} to core, vectored {1} level {2} privilege {3}", bestInterrupt, bestVectored, bestLevel, bestPrivilege);
                return true;
            }
            else
            {
                cpu.ClicPresentInterrupt(NoInterrupt, false, MinLevel, PrivilegeLevel.User);
                acknowledgedInterrupt = NoInterrupt;
                return false;
            }
        }

        private void LogUnhandledIndirectCSRRead(uint iselect, uint ireg)
        {
            this.WarningLog("Unhandled read from register via indirect CSR access (iselect 0x{0:x}, ireg {1})", iselect, ireg);
        }

        private void LogUnhandledIndirectCSRWrite(uint iselect, uint ireg)
        {
            this.WarningLog("Unhandled write to register via indirect CSR access (iselect 0x{0:x}, ireg {1})", iselect, ireg);
        }

        protected override void DefineRegisters()
        {
            var configOffset = configurationHasNvbits ? 1 : 0;
            Register.Configuration.Define32(this)
                .If(configurationHasNvbits).Then(r => r.WithFlag(0, FieldMode.Read, valueProviderCallback: _ => true, name: "nvbits")).EndIf()
                .WithValueField(configOffset + 0, 4, out machineLevelBits, name: "mnlbits")
                .WithValueField(configOffset + 4, 2, out modeBits, name: "nmbits", changeCallback: (_, value) =>
                {
                    if(value == 3)
                    {
                        this.WarningLog("A value of 3 for nmbits is reserved, forcing to 2");
                        modeBits.Value = 2;
                    }
                })
                .WithReservedBits(configOffset + 6, 10)
                .WithValueField(configOffset + 16, 4, out supervisorLevelBits, name: "snlbits")
                .WithReservedBits(configOffset + 20, 4)
                .WithTag("unlbits", configOffset + 24, 4)
                .WithReservedBits(configOffset + 28, configurationHasNvbits ? 3 : 4)
            ;

            Register.Information.Define32(this)
                .WithValueField(0, 13, FieldMode.Read, valueProviderCallback: _ => numberOfInterrupts, name: "num_interrupt")
                .WithValueField(13, 8, FieldMode.Read, valueProviderCallback: _ => 0, name: "version")
                .WithValueField(21, 4, FieldMode.Read, valueProviderCallback: _ => InterruptInputControlBits, name: "CLICINTCTLBITS")
                .WithValueField(25, 6, FieldMode.Read, valueProviderCallback: _ => numberOfTriggers, name: "num_trigger")
                .WithReservedBits(31, 1)
            ;

            Register.InterruptTrigger0.Define32Many(this, numberOfTriggers, (register, index) =>
            {
                register
                    .WithTag("interrupt_number", 0, 13)
                    .WithReservedBits(13, 17)
                    .WithTaggedFlag("nxti_enable", 30)
                    .WithTaggedFlag("enable", 31)
                ;
            });

            Register.InterruptPending0.Define8Many(this, numberOfInterrupts, (register, index) =>
            {
                register
                    .WithFlag(0, out interruptPending[index], name: "pending", changeCallback: (oldValue, value) =>
                    {
                        if(!edgeTriggered[index].Value)
                        {
                            this.WarningLog("Changing the pending bit of level-triggered interrupt #{0} ({1} -> {2}) is not allowed", index, oldValue, value);
                            interruptPending[index].Value = oldValue;
                            return;
                        }
                        UpdateInterrupt();
                        this.DebugLog("Set interrupt #{0} pending {1}", index, interruptPending[index].Value);
                    })
                    .WithReservedBits(1, 7);
                ;
            }, 4);

            Register.InterruptEnable0.Define8Many(this, numberOfInterrupts, (register, index) =>
            {
                register
                    .WithFlag(0, out interruptEnable[index], name: "enable", changeCallback: (_, value) =>
                    {
                        UpdateInterrupt();
                        this.DebugLog("Set interrupt #{0} enabled {1}", index, value);
                    })
                    .WithReservedBits(1, 7)
                ;
            }, 4);

            Register.InterruptAttribute0.Define8Many(this, numberOfInterrupts, (register, index) =>
            {
                register
                    .WithFlag(0, out vectored[index], name: "shv")
                    .WithFlag(1, out edgeTriggered[index], name: "edge_triggered") // 0=level, 1=edge
                    .WithFlag(2, out negative[index], name: "negative") // 0=positive (rising), 1=negative (falling)
                    .WithReservedBits(3, 3)
                    .WithValueField(6, 2, out mode[index], name: "mode")
                    .WithChangeCallback((_, __) =>
                    {
                        UpdateInterrupt();
                        this.DebugLog("Set interrupt #{0} edge-triggered {1} negative {2}", index, edgeTriggered[index].Value, negative[index].Value);
                    })
                ;
            }, 4, resetValue: (byte)PrivilegeLevel.Machine << 6);

            Register.InterruptInputControl0.Define8Many(this, numberOfInterrupts, (register, index) =>
            {
                register
                    .WithValueField(0, InterruptInputControlBits, out inputControl[index], name: "input_control")
                    .WithChangeCallback((_, __) => UpdateInterrupt());
                ;
            }, 4);
        }

        DoubleWordRegisterCollection IProvidesRegisterCollection<DoubleWordRegisterCollection>.RegistersCollection => DoubleWordRegisters;

        private DoubleWordRegisterCollection DoubleWordRegisters { get; }

        private int bestInterrupt;
        private int acknowledgedInterrupt;

        private readonly IFlagRegisterField[] interruptPending;
        private readonly IFlagRegisterField[] interruptEnable;
        private readonly IFlagRegisterField[] vectored;
        private readonly IFlagRegisterField[] edgeTriggered;
        private readonly IFlagRegisterField[] negative;
        private readonly IValueRegisterField[] mode;
        private readonly IValueRegisterField[] inputControl;
        private IValueRegisterField machineLevelBits;
        private IValueRegisterField supervisorLevelBits;
        private IValueRegisterField modeBits;

        private readonly BaseRiscV cpu;
        private readonly uint numberOfInterrupts;
        private readonly uint numberOfTriggers;
        private readonly ulong defaultMachineLevelBits;
        private readonly ulong defaultSupervisorLevelBits;
        private readonly ulong defaultModeBits;
        private readonly bool configurationHasNvbits;

        private const int InterruptInputControlBits = 8; // CLICINTCTLBITS
        private const int MinLevel = 0;
        private const int NoInterrupt = -1;

        // Relative offsets for the various register groups in the indirect CSR space
        private const uint InterruptControlAttribute = 0x000; // - 0x3FF
        private const uint InterruptPendingEnable    = 0x400; // - 0x47F
        private const uint InterruptTrigger          = 0x480; // - 0x49F
        private const uint ClicConfiguration         = 0x4A0;

        private enum Register
        {
            Configuration           = 0x0000,
            Information             = 0x0004,
            Reserved0               = 0x0008, // - 0x003F
            InterruptTrigger0       = 0x0040, // - 0x00BC
            Reserved1               = 0x00C0, // - 0x07FF
            Custom                  = 0x0800, // - 0x0FFF
            InterruptPending0       = 0x1000, // 0x1000 + 4 * i
            InterruptEnable0        = 0x1001, // 0x1001 + 4 * i
            InterruptAttribute0     = 0x1002, // 0x1002 + 4 * i
            InterruptInputControl0  = 0x1003, // 0x1003 + 4 * i
        }
    }
}
