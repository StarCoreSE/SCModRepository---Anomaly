﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.WorldEnvironment.Modules;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Network;
using VRage.ModAPI;
using VRage.Network;
using VRage.Sync;
using VRageMath;
using VRage.Utils;
using VRage.ObjectBuilders;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.ModAPI;

namespace StarCore.StructuralIntegrity
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Collector), false, "SI_Field_Gen")]
    public class SI_Core : MyGameLogicComponent, IMyEventProxy
    {
        //Block Var Init and Access to other parts
        public IMyCollector SIGenBlock;
        public MyPoweredCargoContainerDefinition SIGenBlockDef;

        public readonly Config_Settings Config = new Config_Settings();
        public readonly SI_Settings Settings = new SI_Settings();

        SI_Utility Mod => SI_Utility.Instance;

        //Utility Declarations 
        public const string ControlPrefix = "SI_Control.";
        public readonly Guid SettingsGUID = new Guid("9EFDABA1-E705-4F62-BD37-A4B046B60BC0");
        public const int SettingsUpdateCount = (60 * 1) / 10;
        int SyncCountdown;

        //Regular Structural Integrity Values Init
        public float MinFieldPower;
        public float MaxFieldPower;
        public float MinGridModifier;
        public float MaxGridModifier;
        public float ReferenceGridModifier = 0f;

        //Siege Mode Values Init
        public bool SiegeEnabled;
        public float SiegeMinPowerReq;
        public int SiegeTimer;
        public int SiegeCooldownTimer;

        public int SiegeVisibleTimer;
        public const int SiegeDisplayTimer = 60;
        public int CountSiegeDisplayTimer;

        // Internal
        MySync<bool, SyncDirection.BothWays> SiegeActive = null;
        public bool SiegeCooldownActive = false;
        public bool SiegeModeModifier = false;

        //General Var Declarations
        public float MaxAvailableGridPower = 0f;

        private MyResourceSinkComponent Sink = null;

        private IMyHudNotification notifFieldPower = null;
        private IMyHudNotification notifCountdown = null;

        public float CurrentFieldPower
        {
            get
            { return Settings.FieldPower; }
            set
            {
                Settings.FieldPower = MathHelper.Clamp((float)Math.Floor(value), MinFieldPower, MaxFieldPower);

                SettingsChanged();
                
                if ((NeedsUpdate & MyEntityUpdateEnum.EACH_10TH_FRAME) == 0)
                    NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;

                SIGenBlock?.Components?.Get<MyResourceSinkComponent>()?.Update();
            }
        }

        public float CurrentGridModifier
        {
            get
            { return Settings.GridModifier; }
            set
            {
                Settings.GridModifier = MathHelper.Clamp((float)Math.Round(value, 2), MaxGridModifier, MinGridModifier);

                SettingsChanged();

                if ((NeedsUpdate & MyEntityUpdateEnum.EACH_10TH_FRAME) == 0)
                    NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;

                SIGenBlock?.Components?.Get<MyResourceSinkComponent>()?.Update();
            }
        }

        #region Overrides
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            try
            {
                NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
            }
            catch (Exception e)
            {
                Log.Error($"\nException in Init:\n{e}");
            }
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                // Assign Config Values to previously initialized vars
                RetrieveValuesFromConfig();

                //Assign as Entity
                SIGenBlock = (IMyCollector)Entity;

                if (SIGenBlock.CubeGrid?.Physics == null)
                    return;

                //Assign Definition to PoweredCargo for Power Override
                SIGenBlockDef = (MyPoweredCargoContainerDefinition)SIGenBlock.SlimBlock.BlockDefinition;

                Sink = SIGenBlock.Components.Get<MyResourceSinkComponent>();
                Sink.SetRequiredInputFuncByType(MyResourceDistributorComponent.ElectricityId, RequiredInput);

                // Dont know why this exists yet
                float minDivertedPower = MinFieldPower;
                float maxDivertedPower = MaxFieldPower; 
                SetupTerminalControls<IMyCollector>(minDivertedPower, maxDivertedPower);;

                // Apply Defaults
                CurrentFieldPower = MinFieldPower;
                CurrentGridModifier = MinGridModifier;
                SiegeActive.Value = false;

                LoadSettings();

                SaveSettings();

                NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;
            }
            catch (Exception e)
            {
                Log.Error($"\nException in UpdateOnceBefore:\n{e}");
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            try
            {
                SyncSettings();

                if (SiegeCooldownActive == true)
                {
                    if (SiegeCooldownTimer > 0)
                    {
                        SiegeCooldownTimer = SiegeCooldownTimer - 10;
                    }
                    else if (SiegeCooldownTimer <= 0)
                    {
                        RetrieveValuesFromConfig();
                        SiegeCooldownActive = false;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"\nException in UpdateBeforeSimulation10\n{e}");
            }
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if (SiegeEnabled)
                {
                    SiegeMode(SiegeActive);
                }

                CalculateMaxGridPower();
                UpdateGridModifier(SIGenBlock);
            }
            catch (Exception e)
            {
                Log.Error($"\nException in UpdateAfterSimulation\n{e}");
            }
        }

        public override void Close()
        {
            try
            {
                if (SIGenBlock == null)
                    return;

                if (SiegeActive.Value)
                {
                    SetCountStatus($"Block Removed! Siege Mode Deactivated", 1500, MyFontEnum.Red);
                }

                ResetGridModifier(SIGenBlock);
                SIGenBlock = null;
            }
            catch (Exception e)
            {
                Log.Error($"\nException in Close\n{e}");
            }
        }
        #endregion

        #region Utilities
        public void RetrieveValuesFromConfig()
        {
            // Assign General Values from Config
            if (MinFieldPower != Config.MinFieldPower)
                MinFieldPower = Config.MinFieldPower;

            if (MaxFieldPower != Config.MaxFieldPower)
                MaxFieldPower = Config.MaxFieldPower;

            if (MinGridModifier != Config.MinGridModifier)
                MinGridModifier = Config.MinGridModifier;

            if (MaxGridModifier != Config.MaxGridModifier)
                MaxGridModifier = Config.MaxGridModifier;

            // Assign Siege Specific Values from Config
            if (SiegeEnabled != Config.SiegeEnabled)
                SiegeEnabled = Config.SiegeEnabled;

            if (SiegeMinPowerReq != Config.SiegeMinPowerReq)
                SiegeMinPowerReq = Config.SiegeMinPowerReq;

            if (SiegeTimer != Config.SiegeTimer)
                SiegeTimer = Config.SiegeTimer;

            if (SiegeCooldownTimer != Config.SiegeCooldownTimer)
                SiegeCooldownTimer = Config.SiegeCooldownTimer;

            // Calculate Visible Time from Ticks / 60 for Seconds
            CountSiegeDisplayTimer = SiegeDisplayTimer;
            SiegeVisibleTimer = SiegeTimer / SiegeDisplayTimer;
        }

        private float RequiredInput()
        {
            if (!SIGenBlock.IsWorking)
                return 0f;

            if (CurrentFieldPower == 0f && !SiegeActive.Value)
            {
                return 50.000f;
            }
            else if (SiegeActive.Value)
            {
                CalculateMaxGridPower();

                float maxPowerUsage = SIGenBlockDef.RequiredPowerInput = MaxAvailableGridPower * 0.9f;

                return maxPowerUsage;
            }
            else
            {
                CalculateMaxGridPower();

                float baseUsage = 50.000f;
                float powerPrecentage = SIGenBlockDef.RequiredPowerInput = MaxAvailableGridPower * 0.3f;
                float sliderValue = CurrentFieldPower;

                float ratio = sliderValue / MaxFieldPower;

                return baseUsage + ((baseUsage + (powerPrecentage - baseUsage)) * ratio);
            }
        }

        private void CalculateMaxGridPower()
        {
            if (!SIGenBlock.IsWorking || SIGenBlock.GameLogic == null)
                return;

            var blockLogic = SIGenBlock.GameLogic.GetAs<SI_Core>();
            if (blockLogic == null)
                return;

            float totalPower = 0f;
            var blocks = new List<IMySlimBlock>();
            SIGenBlock.CubeGrid.GetBlocks(blocks);

            foreach (var block in blocks)
            {
                if(block.FatBlock != null)
                {
                    var fatBlock = block.FatBlock;
                    if (fatBlock is IMyPowerProducer)
                    {
                        var powerProducer = fatBlock as IMyPowerProducer;
                        totalPower += powerProducer.MaxOutput;
                    }
                }
            }

            if (MaxAvailableGridPower != totalPower)
                MaxAvailableGridPower = totalPower;
        }

        private void DisplayMessageToNearPlayers(int msgId)
        {
            if (SIGenBlock != null)
            {
                var bound = new BoundingSphereD(SIGenBlock.GetPosition(), 50);
                List<IMyEntity> nearEntities = MyAPIGateway.Entities.GetEntitiesInSphere(ref bound);

                foreach (var entity in nearEntities)
                {
                    IMyCharacter character = entity as IMyCharacter;
                    if (character != null && character.IsPlayer && bound.Contains(character.GetPosition()) != ContainmentType.Disjoint)
                    {
                        if (msgId == 0)
                        {
                            SetCountStatus($"Siege Mode: " + SiegeVisibleTimer + " Seconds", 1500, MyFontEnum.Green);
                        }
                        else if (msgId == 1)
                        {
                            SetCountStatus($"Siege Mode Deactivated", 1500, MyFontEnum.Red);
                        }
                        else if (msgId == 2)
                        {
                            SetCountStatus($"Block Inoperative! Siege Mode Deactivated", 1500, MyFontEnum.Red);
                        }
                        else
                        {
                            SetCountStatus($"Error! Unknown State!", 1500, MyFontEnum.Red);
                            return;
                        }

                    }
                }
            }
        }

        private void SetPowerStatus(string text, int aliveTime = 300, string font = MyFontEnum.Green)
        {
            if (notifFieldPower == null)
                notifFieldPower = MyAPIGateway.Utilities.CreateNotification("", aliveTime, font);

            notifFieldPower.Hide();
            notifFieldPower.Font = font;
            notifFieldPower.Text = text;
            notifFieldPower.AliveTime = aliveTime;
            notifFieldPower.Show();
        }

        public void SetCountStatus(string text, int aliveTime = 300, string font = MyFontEnum.Green)
        {
            if (notifCountdown == null)
                notifCountdown = MyAPIGateway.Utilities.CreateNotification("", aliveTime, font);

            notifCountdown.Hide();
            notifCountdown.Font = font;
            notifCountdown.Text = text;
            notifCountdown.AliveTime = aliveTime;
            notifCountdown.Show();
        }
        #endregion

        #region General Function
        private void UpdateGridModifier(IMyTerminalBlock obj)
        {
            if (obj.EntityId != SIGenBlock.EntityId) return;

            if (obj != null && obj.IsWorking)
            {
                if (!SiegeActive.Value && MaxAvailableGridPower <= SiegeMinPowerReq)
                {
                    SetCountStatus($"Insufficient Power", 1500, MyFontEnum.Red);
                    CurrentGridModifier = 1.0f;
                    return;
                }
                else if (!SiegeActive.Value && MaxAvailableGridPower >= SiegeMinPowerReq)
                {
                    var blockLogic = obj.GameLogic?.GetAs<SI_Core>();
                    if (blockLogic != null)
                    {
                        float currentFieldPower = blockLogic.CurrentFieldPower;

                        float value1 = (currentFieldPower - MinFieldPower) / (float)MaxFieldPower - MinFieldPower;
                        float newGridModifier = MinGridModifier + value1 * (MaxGridModifier - MinGridModifier);

                        newGridModifier = (float)Math.Round(newGridModifier, 2);

                        CurrentGridModifier = newGridModifier;

                        if (CurrentGridModifier == ReferenceGridModifier)
                            return;
                        else
                        {
                            Sink.Update();

                            MyVisualScriptLogicProvider.SetGridGeneralDamageModifier(obj.CubeGrid.Name, newGridModifier);

                            ReferenceGridModifier = newGridModifier;

                            SetPowerStatus($"Integrity Field Power: " + CurrentFieldPower + "%", 1500, MyFontEnum.Green);
                        }
                    }
                }
            }
            else if (!SIGenBlock.IsWorking && !SiegeActive.Value)
            {
                if (CurrentFieldPower > 0f)
                {
                    CurrentFieldPower = 0f;
                    CurrentGridModifier = 1.0f;
                    ResetGridModifier(SIGenBlock);
                }
                else
                    return;
            }

        }

        private void ResetGridModifier(IMyTerminalBlock obj)
        {
            if (obj.EntityId != SIGenBlock.EntityId) return;

            MyVisualScriptLogicProvider.SetGridGeneralDamageModifier(obj.CubeGrid.Name, 1f);
        }
        #endregion

        #region Siege Mode
        private void SiegeMode(MySync<bool, SyncDirection.BothWays> SiegeActivated)
        {
            if (!SiegeActivated.Value || SIGenBlock == null)
                return;

            var allTerminalBlocks = new List<IMySlimBlock>();
            SIGenBlock.CubeGrid.GetBlocks(allTerminalBlocks);

            if (MaxAvailableGridPower <= SiegeMinPowerReq)
            {
                SetCountStatus($"Insufficient Power", 1500, MyFontEnum.Red);
                SiegeActivated.Value = false;
                return;
            }

            if (!SiegeModeModifier && !SIGenBlock.IsWorking && MaxAvailableGridPower > SiegeMinPowerReq)
            {
                SetCountStatus($"Block Disabled", 1500, MyFontEnum.Red);
                SiegeActivated.Value = false;
                return;
            }

            if (!SiegeModeModifier && SIGenBlock.IsWorking && MaxAvailableGridPower > 150f)
            {
                MyVisualScriptLogicProvider.SetGridGeneralDamageModifier(SIGenBlock.CubeGrid.Name, 0.1f);
                SiegeModeModifier = true;
            }

            if (SiegeModeModifier && SIGenBlock.IsWorking && MaxAvailableGridPower > 150f)
            {

                Sink.Update();

                if (SiegeTimer > 0)
                {
                    SiegeBlockShutdown(allTerminalBlocks);

                    if (SIGenBlock.CubeGrid.Physics.LinearVelocity != Vector3D.Zero)
                    {
                        Vector3D linearVelocity = SIGenBlock.CubeGrid.Physics.LinearVelocity;
                        Vector3D oppositeVector = new Vector3D(-linearVelocity.X, -linearVelocity.Y, -linearVelocity.Z);
                        SIGenBlock.CubeGrid.Physics.LinearVelocity = oppositeVector;
                    }

                    SiegeTimer = SiegeTimer - 1;
                    CountSiegeDisplayTimer = CountSiegeDisplayTimer - 1;
                    if (CountSiegeDisplayTimer <= 0)
                    {
                        CountSiegeDisplayTimer = SiegeDisplayTimer;
                        SiegeVisibleTimer = SiegeVisibleTimer - 1;
                        DisplayMessageToNearPlayers(0);
                    }
                }
                else
                {
                    RetrieveValuesFromConfig();

                    ResetGridModifier(SIGenBlock);
                    SiegeBlockReboot(allTerminalBlocks);
                    DisplayMessageToNearPlayers(1);

                    SiegeActivated.Value = false;
                    SiegeModeModifier = false;
                    SiegeCooldownActive = true;

                    Sink.Update();
                }
            }
            else if (!SIGenBlock.IsWorking && SiegeActivated.Value)
            {
                RetrieveValuesFromConfig();

                ResetGridModifier(SIGenBlock);
                SiegeBlockReboot(allTerminalBlocks);
                DisplayMessageToNearPlayers(2);

                SiegeActivated.Value = false;
                SiegeModeModifier = false;
                SiegeCooldownActive = true;

                Sink.Update();
            }
        }

        private void SiegeBlockShutdown(List<IMySlimBlock> allTerminalBlocks)
        {
            foreach (var block in allTerminalBlocks)
            {
                if (block.FatBlock is IMyReactor || block.FatBlock is IMyBatteryBlock ||
                    block.FatBlock is IMySolarPanel || block.FatBlock is IMyWindTurbine ||
                    block.FatBlock is IMyCollector || block.FatBlock is IMyCockpit)
                {
                    continue;
                }

                var functionalBlock = block.FatBlock as IMyFunctionalBlock;
                if (functionalBlock != null)
                {
                    functionalBlock.Enabled = false;
                }
            }
        }

        private void SiegeBlockReboot(List<IMySlimBlock> allTerminalBlocks)
        {
            foreach (var block in allTerminalBlocks)
            {
                if (block.FatBlock is IMyReactor || block.FatBlock is IMyBatteryBlock ||
                    block.FatBlock is IMySolarPanel || block.FatBlock is IMyWindTurbine ||
                    block.FatBlock is IMyCollector || block.FatBlock is IMyCockpit)
                {
                    continue;
                }

                var functionalBlock = block.FatBlock as IMyFunctionalBlock;
                if (functionalBlock != null)
                {
                    functionalBlock.Enabled = true;
                }
            }
        }
        #endregion

        #region Settings
        bool LoadSettings()
        {
            if (SIGenBlock.Storage == null)
                return false;

            string rawData;
            if (!SIGenBlock.Storage.TryGetValue(SettingsGUID, out rawData))
                return false;

            try
            {
                var loadedSettings = MyAPIGateway.Utilities.SerializeFromBinary<SI_Settings>(Convert.FromBase64String(rawData));

                if (loadedSettings != null)
                {
                    Settings.FieldPower = loadedSettings.FieldPower;
                    Settings.GridModifier = loadedSettings.GridModifier;
                    return true;
                }
            }
            catch (Exception e)
            {
                Log.Error($"Error loading settings!\n{e}");
            }

            return false;
        }

        void SaveSettings()
        {
            try
            {
                if (SIGenBlock == null)
                    return; // called too soon or after it was already closed, ignore

                if (Settings == null)
                    throw new NullReferenceException($"Settings == null on entId={Entity?.EntityId}; modInstance={SI_Utility.Instance != null}");

                if (MyAPIGateway.Utilities == null)
                    throw new NullReferenceException($"MyAPIGateway.Utilities == null; entId={Entity?.EntityId}; modInstance={SI_Utility.Instance != null}");

                if (SIGenBlock.Storage == null)
                    SIGenBlock.Storage = new MyModStorageComponent();

                SIGenBlock.Storage.SetValue(SettingsGUID, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Settings)));
            }
            catch (Exception e)
            {
                Log.Error($"Error saving settings!\n{e}");
            }
        }

        void SettingsChanged()
        {
            if (SyncCountdown == 0)
            {
                SyncCountdown = SettingsUpdateCount;
            }
        }

        void SyncSettings()
        {
            try
            {
                if (SyncCountdown > 0 && --SyncCountdown <= 0)
                {
                    SaveSettings();

                    Mod.CachedPacketSettings.Send(SIGenBlock.EntityId, Settings);
                }
            }
            catch (Exception e)
            {
                Log.Error($"Error syncing settings!\n{e}");
            }
        }

        public override bool IsSerialized()
        {
            try
            {
                SaveSettings();
            }
            catch (Exception e)
            {
                Log.Error(e);
            }

            return base.IsSerialized();
        }
        #endregion

        #region Terminal Controls
        static void SetupTerminalControls<T>(float minDivertedPower, float maxDivertedPower)
        {
            var mod = SI_Utility.Instance;

            if (mod.ControlsCreated)
                return;

            mod.ControlsCreated = true;

            #region SiegeToggle
            var siegeModeToggle = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyCollector>(ControlPrefix + "SiegeMode");
            siegeModeToggle.Title = MyStringId.GetOrCompute("Siege Mode");
            siegeModeToggle.Tooltip = MyStringId.GetOrCompute("Toggle Siege Mode");
            siegeModeToggle.OnText = MyStringId.GetOrCompute("On");
            siegeModeToggle.OffText = MyStringId.GetOrCompute("Off");
            siegeModeToggle.Visible = Siege_Enabled_Enabler;
            siegeModeToggle.Enabled = Siege_Cooldown_Enabler;
            siegeModeToggle.Getter = (b) => b.GameLogic.GetAs<SI_Core>().SiegeActive.Value;
            siegeModeToggle.Setter = (b, v) => b.GameLogic.GetAs<SI_Core>().SiegeActive.Value = v;
            siegeModeToggle.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<T>(siegeModeToggle);
            #endregion

            #region Slider
            var currentFieldPowerSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyCollector>(ControlPrefix + "FieldPowerSlider");
            currentFieldPowerSlider.Title = MyStringId.GetOrCompute("Integrity Field Power");
            currentFieldPowerSlider.Tooltip = MyStringId.GetOrCompute("Set Damage Absorption Percentage");
            currentFieldPowerSlider.SetLimits(minDivertedPower, maxDivertedPower);
            currentFieldPowerSlider.Visible = Control_Visible;
            currentFieldPowerSlider.Enabled = Siege_Status_Enabler;
            currentFieldPowerSlider.Writer = Control_Power_Writer;
            currentFieldPowerSlider.Getter = Control_Power_Getter;
            currentFieldPowerSlider.Setter = Control_Power_Setter;
            currentFieldPowerSlider.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<T>(currentFieldPowerSlider);
            #endregion

            #region Increase Action
            var increaseFieldPower = MyAPIGateway.TerminalControls.CreateAction<IMyCollector>(ControlPrefix + "FieldPowerIncrease");
            increaseFieldPower.Name = new StringBuilder("Increase Field Power");
            increaseFieldPower.ValidForGroups = false;
            increaseFieldPower.Icon = @"Textures\GUI\Icons\Actions\Increase.dds";
            increaseFieldPower.Action = (b) =>
            {
                var logic = b?.GameLogic?.GetAs<SI_Core>();
                if (logic != null)
                {
                    if (logic.SiegeActive.Value)
                    {
                        logic.SetPowerStatus($"Cant Change Field Power in Siege Mode", 1500, MyFontEnum.Red);
                        return;
                    }
                    if (logic.SIGenBlock.IsWorking == false)
                    {
                        logic.SetPowerStatus($"Block Disabled", 1500, MyFontEnum.Red);
                        return;
                    }
                    logic.CurrentFieldPower = logic.CurrentFieldPower + 1;
                    logic.CurrentFieldPower = MathHelper.Clamp(logic.CurrentFieldPower, 0f, 30f);
                }
            };
            increaseFieldPower.Writer = (b, sb) =>
            {
                var logic = b?.GameLogic?.GetAs<SI_Core>();
                if (logic != null)
                {
                    sb.Append("Increase");
                }
            };
            increaseFieldPower.InvalidToolbarTypes = new List<MyToolbarType>()
            {
                MyToolbarType.ButtonPanel,
                MyToolbarType.Character,
            };
            MyAPIGateway.TerminalControls.AddAction<T>(increaseFieldPower);
            #endregion

            #region Decrease Action
            var decreaseFieldPower = MyAPIGateway.TerminalControls.CreateAction<IMyCollector>(ControlPrefix + "FieldPowerDecrease");
            decreaseFieldPower.Name = new StringBuilder("Decrease Field Power");
            decreaseFieldPower.ValidForGroups = false;
            decreaseFieldPower.Icon = @"Textures\GUI\Icons\Actions\Decrease.dds";
            decreaseFieldPower.Action = (b) =>
            {
                var logic = b?.GameLogic?.GetAs<SI_Core>();
                if (logic != null)
                {
                    if (logic.SiegeActive.Value)
                    {
                        logic.SetPowerStatus($"Cant Change Field Power in Siege Mode", 1500, MyFontEnum.Red);
                        return;
                    }
                    if (logic.SIGenBlock.IsWorking == false)
                    {
                        logic.SetPowerStatus($"Block Disabled", 1500, MyFontEnum.Red);
                        return;
                    }
                    logic.CurrentFieldPower = logic.CurrentFieldPower - 1;
                    logic.CurrentFieldPower = MathHelper.Clamp(logic.CurrentFieldPower, 0f, 30f);
                }
            };
            decreaseFieldPower.Writer = (b, sb) =>
            {
                var logic = b?.GameLogic?.GetAs<SI_Core>();
                if (logic != null)
                {
                    sb.Append("Decrease");
                }
            };
            decreaseFieldPower.InvalidToolbarTypes = new List<MyToolbarType>()
            {
                MyToolbarType.ButtonPanel,
                MyToolbarType.Character,
            };
            MyAPIGateway.TerminalControls.AddAction<T>(decreaseFieldPower);
            #endregion

            #region Toggle Action
            var siegeModeToggleAction = MyAPIGateway.TerminalControls.CreateAction<IMyCollector>(ControlPrefix + "SiegeModeToggle");
            siegeModeToggleAction.Name = new StringBuilder("Siege Mode");
            siegeModeToggleAction.ValidForGroups = false;
            siegeModeToggleAction.Icon = @"Textures\GUI\Icons\Actions\Toggle.dds";
            siegeModeToggleAction.Enabled = Siege_Enabled_Enabler;
            siegeModeToggleAction.Action = (b) =>
            {
                var logic = b?.GameLogic?.GetAs<SI_Core>();
                if (logic != null)
                {
                    if (logic.SiegeActive.Value == true)
                    {
                        logic.SetPowerStatus($"Cant Deactivate Siege Mode", 1500, MyFontEnum.Red);
                        return;
                    }
                    else if (logic.SiegeCooldownActive == true)
                    {
                        logic.SetCountStatus($"Siege Mode On Cooldown: " + (logic.SiegeCooldownTimer / 60) + " Seconds", 1500, MyFontEnum.Red);
                        return;
                    }
                    if (logic.SiegeActive.Value == false)
                    {
                        Log.Info($"Siege Action: Set to True");
                        logic.SiegeActive.Value = true;
                        /*logic.Settings.SiegeModeActivated = logic.SiegeModeActivated_MySync.Value;*/
                        Log.Info($"Siege Action: Current Settings Value: {logic.SiegeActive.Value}");
                    }
                    else
                        return;
                }
            };
            siegeModeToggleAction.Writer = (b, sb) =>
            {
                var logic = b?.GameLogic?.GetAs<SI_Core>();
                if (logic != null)
                {
                    sb.Append("Siege Mode");
                }
            };
            siegeModeToggleAction.InvalidToolbarTypes = new List<MyToolbarType>()
            {
                MyToolbarType.ButtonPanel,
                MyToolbarType.Character,
            };
            MyAPIGateway.TerminalControls.AddAction<T>(siegeModeToggleAction);
            #endregion
        }

        static SI_Core GetLogic(IMyTerminalBlock block) => block?.GameLogic?.GetAs<SI_Core>();

        static bool Control_Visible(IMyTerminalBlock block)
        {
            return GetLogic(block) != null;
        }

        static bool Siege_Enabled_Enabler(IMyTerminalBlock block)
        {
            // Validate the input block is not null
            if (block == null)
            {
                // Log error or handle it as per requirement
                return false; // or a sensible default considering the game's behavior
            }

            // Safely retrieve the SI_Core logic component
            SI_Core blockLogic = GetLogic(block);
            if (blockLogic == null)
            {
                // Log error or handle it as per requirement
                return false; // or a sensible default considering the game's behavior
            }

            // Safely return the SiegeEnabled property
            // Assuming SiegeEnabled is a non-nullable bool; otherwise, further null-check might be necessary
            return blockLogic.SiegeEnabled;
        }


        static bool Siege_Status_Enabler(IMyTerminalBlock block)
        {
            if (GetLogic(block) != null)
            {
                SI_Core blockLogic = GetLogic(block);

                if (blockLogic.SiegeActive.Value == true)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
            return false;
        }

        static bool Siege_Cooldown_Enabler(IMyTerminalBlock block)
        {
            if (GetLogic(block) != null)
            {
                SI_Core dynamicResistLogic = GetLogic(block);

                if (dynamicResistLogic.SiegeCooldownActive == true || dynamicResistLogic.SiegeActive.Value == true)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }

            return false;
        }

        static float Control_Power_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return logic != null ? logic.CurrentFieldPower : 0f;
        }

        static void Control_Power_Setter(IMyTerminalBlock block, float value)
        {
            var logic = GetLogic(block);
            if (logic != null)
            {
                logic.CurrentFieldPower = MathHelper.Clamp(value, 0f, 30f);
                logic.CurrentFieldPower = (float)Math.Round(logic.CurrentFieldPower, 0);
            }
        }

        static void Control_Power_Writer(IMyTerminalBlock block, StringBuilder writer)
        {
            var logic = GetLogic(block);
            if (logic != null)
            {
                float value = logic.CurrentFieldPower;
                writer.Append(Math.Round(value, 0, MidpointRounding.ToEven)).Append("%");
            }
        }
        #endregion
    }
}