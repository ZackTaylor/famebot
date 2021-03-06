﻿using Lib_K_Relay.Interface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lib_K_Relay;
using System.Diagnostics;
using FameBot.Data.Enums;
using FameBot.Data.Models;
using Lib_K_Relay.Networking;
using System.Runtime.InteropServices;
using FameBot.Helpers;
using Lib_K_Relay.Networking.Packets;
using Lib_K_Relay.Networking.Packets.Server;
using Lib_K_Relay.Networking.Packets.DataObjects;
using Lib_K_Relay.Utilities;
using Lib_K_Relay.Networking.Packets.Client;
using Lib_K_Relay.GameData;
using FameBot.Services;
using FameBot.UserInterface;
using FameBot.Data.Events;
using System.Drawing;

namespace FameBot.Core
{
    public class Plugin : IPlugin
    {
        #region IPlugin
        public string GetAuthor()
        {
            return "tcrane";
        }

        public string[] GetCommands()
        {
            return new string[]
            {
                "/bind - binds the bot to the client where the command is used.",
                "/start - starts the bot",
                "/gui - opens the gui"
            };
        }

        public string GetDescription()
        {
            return "A bot designed to automate the process of collecting fame.";
        }

        public string GetName()
        {
            return "FameBot by tcrane";
        }
        #endregion

        private IntPtr flashPtr;
        private bool followTarget;
        private List<Target> targets;
        private List<Portal> portals;
        private Dictionary<int, Target> playerPositions;
        private List<Enemy> enemies;
        private List<Rock> rocks;
        private Client connectedClient;
        private int tickCount;
        private Configuration config;
        private FameBotGUI gui;
        private bool gotoRealm;
        private bool enabled;
        private string currentMapName;

        public static event HealthEventHandler healthChanged;
        public delegate void HealthEventHandler(object sender, HealthChangedEventArgs args);

        public static event KeyEventHandler keyChanged;
        public delegate void KeyEventHandler(object sender, KeyEventArgs args);

        private static event GuiEventHandler guiEvent;
        private delegate void GuiEventHandler(GuiEvent evt);

        public static event LogEventHandler logEvent;
        public delegate void LogEventHandler(object sender, LogEventArgs args);

        private static event SendMessageEventHandler sendMessage;
        private delegate void SendMessageEventHandler(string message);

        public static event ReceiveMessageEventHandler receiveMesssage;
        public delegate void ReceiveMessageEventHandler(object sender, MessageEventArgs args);

        public static event FameUpdateEventHandler fameUpdate;
        public delegate void FameUpdateEventHandler(object sender, FameUpdateEventArgs args);

        #region WINAPI
        // Get the focused window.
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetForegroundWindow();
        // Send a message to a specific process via the handle.
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

        // Gets the positions of the corners of a window via the MainWindowHandle.
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);
        // Converts a point in screen space to a point relative to hWnd's window.
        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        #endregion

        #region Keys
        private bool wPressed;
        private bool aPressed;
        private bool sPressed;
        private bool dPressed;

        private bool W_PRESSED
        {
            get { return wPressed; }
            set
            {
                if (wPressed == value)
                    return;
                wPressed = value;
                SendMessage(flashPtr, value ? (uint)Key.KeyDown : (uint)Key.KeyUp, new IntPtr((int)Key.W), IntPtr.Zero);
                keyChanged?.Invoke(this, new KeyEventArgs(Key.W, value));
            }
        }
        private bool A_PRESSED
        {
            get { return aPressed; }
            set
            {
                if (aPressed == value)
                    return;
                aPressed = value;
                SendMessage(flashPtr, value ? (uint)Key.KeyDown : (uint)Key.KeyUp, new IntPtr((int)Key.A), IntPtr.Zero);
                keyChanged?.Invoke(this, new KeyEventArgs(Key.A, value));
            }
        }
        private bool S_PRESSED
        {
            get { return sPressed; }
            set
            {
                if (sPressed == value)
                    return;
                sPressed = value;
                SendMessage(flashPtr, value ? (uint)Key.KeyDown : (uint)Key.KeyUp, new IntPtr((int)Key.S), IntPtr.Zero);
                keyChanged?.Invoke(this, new KeyEventArgs(Key.S, value));
            }
        }
        private bool D_PRESSED
        {
            get { return dPressed; }
            set
            {
                if (dPressed == value)
                    return;
                dPressed = value;
                SendMessage(flashPtr, value ? (uint)Key.KeyDown : (uint)Key.KeyUp, new IntPtr((int)Key.D), IntPtr.Zero);
                keyChanged?.Invoke(this, new KeyEventArgs(Key.D, value));
            }
        }
        #endregion

        public void Initialize(Proxy proxy)
        {
            targets = new List<Target>();
            playerPositions = new Dictionary<int, Target>();
            portals = new List<Portal>();
            enemies = new List<Enemy>();
            rocks = new List<Rock>();

            gui = new FameBotGUI();
            PluginUtils.ShowGUI(gui);

            config = ConfigManager.GetConfiguration();

            Process[] processes = Process.GetProcessesByName("flash");
            if (processes.Length == 1)
            {
                Log("Automatically bound to client.");
                flashPtr = processes[0].MainWindowHandle;
                gui?.SetHandle(flashPtr);
                if (config.AutoConnect)
                    Start();
            } else if(processes.Length > 1)
            {
                Log("Multiple flash players running. Use the /bind command on the client you want to use.");
            } else
            {
                Log("Couldn't find flash player. Use the /bind command in game, then start the bot.");
            }

            proxy.HookCommand("bind", ReceiveCommand);
            proxy.HookCommand("start", ReceiveCommand);
            proxy.HookCommand("gui", ReceiveCommand);

            proxy.HookPacket(PacketType.UPDATE, OnUpdate);
            proxy.HookPacket(PacketType.NEWTICK, OnNewTick);
            proxy.HookPacket(PacketType.PLAYERHIT, OnHit);
            proxy.HookPacket(PacketType.MAPINFO, OnMapInfo);
            proxy.HookPacket(PacketType.TEXT, OnText);

            proxy.ClientConnected += (client) =>
            {
                connectedClient = client;
                targets.Clear();
                playerPositions.Clear();
                enemies.Clear();
                rocks.Clear();
                followTarget = false;
                A_PRESSED = false;
                D_PRESSED = false;
                W_PRESSED = false;
                S_PRESSED = false;
            };

            proxy.ClientDisconnected += (client) =>
            {
                Log("Client disconnected. Waiting a few seconds before trying to press play...");
                PressPlay();
            };

            guiEvent += (evt) =>
            {
                switch (evt)
                {
                    case GuiEvent.StartBot:
                        Start();
                        break;
                    case GuiEvent.StopBot:
                        Stop();
                        break;
                    case GuiEvent.SettingsChanged:
                        config = ConfigManager.GetConfiguration();
                        break;
                }
            };

            sendMessage += (message) =>
            {
                if (!(connectedClient?.Connected ?? false))
                    return;
                PlayerTextPacket packet = (PlayerTextPacket)Packet.Create(PacketType.PLAYERTEXT);
                packet.Text = message;
                connectedClient.SendToServer(packet);
            };
        }

        private void ReceiveCommand(Client client, string cmd, string[] args)
        {
            switch(cmd)
            {
                case "bind":
                    flashPtr = GetForegroundWindow();
                    gui?.SetHandle(flashPtr);
                    client.Notify("FameBot is now active");
                    break;
                case "start":
                    Start();
                    client.Notify("FameBot is starting");
                    break;
                case "gui":
                    if (gui == null)
                        gui = new FameBotGUI();
                    gui.Show();
                    gui.SetHandle(flashPtr);
                    break;
            }
        }

        public static void InvokeGuiEvent(GuiEvent evt)
        {
            guiEvent?.Invoke(evt);
        }

        public static void InvokeSendMessageEvent(string message)
        {
            sendMessage?.Invoke(message);
        }

        private void Stop()
        {
            if (!enabled)
                return;
            Log("Stopping bot.");
            followTarget = false;
            gotoRealm = false;
            targets.Clear();
            enabled = false;
        }

        private void Start()
        {
            if (enabled)
                return;
            Log("Starting bot.");
            targets.Clear();
            enabled = true;
            if (currentMapName == null)
                return;
            if (currentMapName.Equals("Nexus") && config.AutoConnect)
            {
                gotoRealm = true;
                followTarget = false;
                if (connectedClient != null)
                    MoveToRealms(connectedClient);
            } else
            {
                gotoRealm = false;
                followTarget = true;
            }
        }

        private void Escape(Client client)
        {
            Log("Escaping to nexus.");
            client.SendToServer(Packet.Create(PacketType.ESCAPE));
        }

        private void Log(string message)
        {
            logEvent?.Invoke(this, new LogEventArgs(message));
        }

        private async void PressPlay()
        {
            await Task.Delay(TimeSpan.FromSeconds(5));

            if (!config.AutoConnect)
                return;
            if (!enabled)
                return;

            if ((connectedClient?.Connected ?? false))
            {
                Log("Client is connected. No need to press play.");
                return;
            }
            else
                Log("Client still not connected. Pressing play button...");

            // Get the window details before pressing the button in case
            // it has changed size or position on the desktop.
            RECT windowRect = new RECT();
            GetWindowRect(flashPtr, ref windowRect);
            var size = windowRect.GetSize();

            // The play button is located half way across the
            // window and roughly 92% of the way to the bottom.
            int playButtonX = size.Width / 2 + windowRect.Left;
            int playButtonY = (int)((double)size.Height * 0.92) + windowRect.Top;

            // Convert the screen point to a window point
            POINT relativePoint = new POINT(playButtonX, playButtonY);
            ScreenToClient(flashPtr, ref relativePoint);

            // Press the buttons.
            SendMessage(flashPtr, (uint)MouseButton.LeftButtonDown, new IntPtr(0x1), new IntPtr((relativePoint.Y << 16) | (relativePoint.X & 0xFFFF)));
            SendMessage(flashPtr, (uint)MouseButton.LeftButtonUp, new IntPtr(0x1), new IntPtr((relativePoint.Y << 16) | (relativePoint.X & 0xFFFF)));

            PressPlay();
        }

        #region PacketHookMethods
        private void OnUpdate(Client client, Packet p)
        {
            UpdatePacket packet = p as UpdatePacket;

            // Get new info
            foreach(Entity obj in packet.NewObjs)
            {
                if(Enum.IsDefined(typeof(Classes), obj.ObjectType))
                {
                    PlayerData playerData = new PlayerData(obj.Status.ObjectId);
                    playerData.Class = (Classes)obj.ObjectType;
                    playerData.Pos = obj.Status.Position;
                    foreach(var data in obj.Status.Data)
                    {
                        playerData.Parse(data.Id, data.IntValue, data.StringValue);
                    }

                    if (playerPositions.ContainsKey(obj.Status.ObjectId))
                        playerPositions.Remove(obj.Status.ObjectId);
                    playerPositions.Add(obj.Status.ObjectId, new Target(obj.Status.ObjectId, playerData.Name, playerData.Pos));
                }
                if(obj.ObjectType == 1810)
                {
                    foreach(var data in obj.Status.Data)
                    {
                        if(data.StringValue != null)
                        {
                            // TODO: replace with regex
                            var strArray = data.StringValue.Split(' ');
                            var strCount = strArray[1].Split('/')[0].Remove(0, 1);
                            var name = strArray[0].Split('.')[1];
                            var portal = new Portal(obj.Status.ObjectId, int.Parse(strCount), name, obj.Status.Position);
                            if (portals.Exists(ptl => ptl.ObjectId == obj.Status.ObjectId))
                                portals.RemoveAll(ptl => ptl.ObjectId == obj.Status.ObjectId);

                            portals.Add(portal);
                        }
                    }
                }
                if(Enum.IsDefined(typeof(EnemyId), (int)obj.ObjectType))
                {
                    if (enemies.Exists(en => en.ObjectId == obj.Status.ObjectId))
                        enemies.RemoveAll(en => en.ObjectId == obj.Status.ObjectId);
                    enemies.Add(new Enemy(obj.Status.ObjectId, obj.Status.Position));
                }

                if(GameData.Objects.ByID((ushort)obj.ObjectType).Name == "Rock Grey")
                {
                    if (!rocks.Exists(rock => rock.ObjectId == obj.Status.ObjectId))
                        rocks.Add(new Rock(obj.Status.ObjectId, obj.Status.Position));
                }
            }
            
            // Remove old info
            foreach (int dropId in packet.Drops)
            {
                if (playerPositions.ContainsKey(dropId))
                {
                    if(followTarget && targets.Exists(t => t.ObjectId == dropId))
                    {
                        targets.Remove(targets.Find(t => t.ObjectId == dropId));
                        if(targets.Count > 0)
                        {
                            Log(string.Format("Dropping \"{0}\" from targets.", playerPositions[dropId].Name));
                        } else
                        {
                            Log("No targets left in target list.");
                            if (config.EscapeIfNoTargets)
                                Escape(client);
                        }
                    }
                    playerPositions.Remove(dropId);
                }

                if (enemies.Exists(en => en.ObjectId == dropId))
                    enemies.RemoveAll(en => en.ObjectId == dropId);

                if (portals.Exists(ptl => ptl.ObjectId == dropId))
                    portals.RemoveAll(ptl => ptl.ObjectId == dropId);
            }
        }

        private void OnMapInfo(Client client, Packet p)
        {
            MapInfoPacket packet = p as MapInfoPacket;
            if (packet == null)
                return;
            portals.Clear();
            currentMapName = packet.Name;

            if (packet.Name == "Oryx's Castle" && enabled)
            {
                Log("Escaping from oryx's castle.");
                Escape(client);
                return;
            }
            if (packet.Name == "Nexus" && config.AutoConnect && enabled)
            {
                gotoRealm = true;
                MoveToRealms(client);
            }
            else
            {
                gotoRealm = false;
                if (enabled)
                    followTarget = true;
            }
        }

        private void OnHit(Client client, Packet p)
        {
            float healthPercentage = (float)client.PlayerData.Health / (float)client.PlayerData.MaxHealth * 100f;
            if (healthPercentage < config.AutonexusThreshold * 1.25f)
                Log(string.Format("Health at {0}%", (int)(healthPercentage)));
        }

        private void OnNewTick(Client client, Packet p)
        {
            NewTickPacket packet = p as NewTickPacket;
            tickCount++;

            // Health changed event
            float healthPercentage = (float)client.PlayerData.Health / (float)client.PlayerData.MaxHealth * 100f;
            healthChanged?.Invoke(this, new HealthChangedEventArgs(healthPercentage));

            // Autonexus
            if (healthPercentage < config.AutonexusThreshold && !(currentMapName?.Equals("Nexus") ?? false) && enabled)
                Escape(client);

            // Fame event
            fameUpdate?.Invoke(this, new FameUpdateEventArgs(client.PlayerData?.CharacterFame ?? -1, client.PlayerData?.CharacterFameGoal ?? -1));

            if (tickCount % config.TickCountThreshold == 0)
            {
                if (followTarget && playerPositions.Count > 0 && !gotoRealm)
                {
                    List<Target> newTargets = D36n4.Invoke(playerPositions.Values.ToList(), config.Epsilon, config.MinPoints, config.FindClustersNearCenter);
                    if(newTargets == null)
                    {
                        if (targets.Count != 0 && config.EscapeIfNoTargets)
                            Escape(client); 
                        targets.Clear();
                        Log("No valid clusters found.");
                    } else
                    {
                        if (targets.Count != newTargets.Count)
                            Log(string.Format("Now targeting {0} players.", newTargets.Count));
                        targets = newTargets;
                    }
                }
                tickCount = 0;
            }

            // Updates
            foreach(Status status in packet.Statuses)
            {
                // Update player positions
                if (playerPositions.ContainsKey(status.ObjectId))
                    playerPositions[status.ObjectId].UpdatePosition(status.Position);

                // Update enemy positions
                if (enemies.Exists(en => en.ObjectId == status.ObjectId))
                    enemies.Find(en => en.ObjectId == status.ObjectId).Location = status.Position;

                // Update portal player counts when in nexus.
                if (portals.Exists(ptl => ptl.ObjectId == status.ObjectId) && (currentMapName?.Equals("Nexus") ?? false))
                {
                    foreach(var data in status.Data)
                    {
                        if(data.StringValue != null)
                        {
                            var strCount = data.StringValue.Split(' ')[1].Split('/')[0].Remove(0, 1);
                            portals[portals.FindIndex(ptl => ptl.ObjectId == status.ObjectId)].PlayerCount = int.Parse(strCount);
                        }
                    }
                }
            }

            if (!followTarget && !gotoRealm)
            {
                W_PRESSED = false;
                A_PRESSED = false;
                S_PRESSED = false;
                D_PRESSED = false;
            }

            if (followTarget && targets.Count > 0)
            {
                var targetPosition = new Location(targets.Average(t => t.Position.X), targets.Average(t => t.Position.Y));

                if (client.PlayerData.Pos.DistanceTo(targetPosition) > config.TeleportDistanceThreshold)
                {
                    var name = targets.OrderBy(t => t.Position.DistanceTo(targetPosition)).First().Name;
                    if (name != client.PlayerData.Name)
                    {
                        var tpPacket = (PlayerTextPacket)Packet.Create(PacketType.PLAYERTEXT);
                        tpPacket.Text = "/teleport " + name;
                        client.SendToServer(tpPacket);
                    }
                }

                if (enemies.Exists(en => en.Location.DistanceSquaredTo(client.PlayerData.Pos) <= 49))
                {
                    Location closestEnemy = enemies.OrderBy(en => en.Location.DistanceSquaredTo(client.PlayerData.Pos)).First().Location;

                    double angle = Math.Atan2(client.PlayerData.Pos.Y - closestEnemy.Y, client.PlayerData.Pos.X - closestEnemy.X);

                    float newX = closestEnemy.X + 8f * (float)Math.Cos(angle);
                    float newY = closestEnemy.Y + 8f * (float)Math.Sin(angle);

                    var avoidPos = new Location(newX, newY);
                    CalculateMovement(client, avoidPos, config.FollowDistanceThreshold);
                    return;
                }

                if(rocks.Exists(rock => rock.Location.DistanceSquaredTo(client.PlayerData.Pos) <= 4))
                {
                    Location closestRock = rocks.OrderBy(rock => rock.Location.DistanceSquaredTo(client.PlayerData.Pos)).First().Location;
                    double angleDifference = client.PlayerData.Pos.GetAngleDifferenceDegrees(targetPosition, closestRock);

                    if (Math.Abs(angleDifference) < 70.0)
                    {
                        double angle = Math.Atan2(client.PlayerData.Pos.Y - closestRock.Y, client.PlayerData.Pos.X - closestRock.X);
                        if(angleDifference <= 0)
                            angle += (Math.PI / 2); // add 90 degrees to the angle to go clockwise around the rock.
                        if (angleDifference > 0)
                            angle -= (Math.PI / 2); // remove 90 degrees from the angle to go anti-clockwise around the rock.

                        float newX = closestRock.X + 2f * (float)Math.Cos(angle);
                        float newY = closestRock.Y + 2f * (float)Math.Sin(angle);

                        var avoidRockPos = new Location(newX, newY);
                        CalculateMovement(client, avoidRockPos, 0.5f);
                        return;
                    }
                }

                CalculateMovement(client, targetPosition, config.FollowDistanceThreshold);
            }
        }

        private void OnText(Client client, Packet p)
        {
            TextPacket packet = p as TextPacket;
            if (packet.Name == client.PlayerData?.Name || packet.NumStars < 1)
                return;
            receiveMesssage?.Invoke(this, new MessageEventArgs(packet.Text, packet.Name, packet.Recipient == client.PlayerData?.Name ? true : false));
        }
        #endregion

        private async void MoveToRealms(Client client)
        {
            if(client == null)
            {
                Log("No client passed to MoveToRealms.");
                return;
            }
            Location target = new Location(159, 101);

            if (client.PlayerData == null)
            {
                await Task.Delay(5);
                MoveToRealms(client);
                return;
            }

            var healthPercentage = (float)client.PlayerData.Health / (float)client.PlayerData.MaxHealth;
            if (healthPercentage < 0.95f)
                target = new Location(159, 127);

            string bestName = "";
            if (client.PlayerData.Pos.Y <= 110 && client.PlayerData.Pos.Y != 0)
            {
                if (portals.Count != 0)
                {
                    int bestCount = 0;
                    if(portals.Where(ptl => ptl.PlayerCount == 85).Count() > 1)
                    {
                        foreach(Portal ptl in portals.Where(ptl => ptl.PlayerCount == 85))
                        {
                            int count = playerPositions.Values.Where(plr => plr.Position.DistanceSquaredTo(ptl.Location) <= 4).Count();
                            if(count > bestCount)
                            {
                                bestCount = count;
                                bestName = ptl.Name;
                                target = ptl.Location;
                            }
                        }
                    } else
                    {
                        Portal ptl = portals.OrderByDescending(prtl => prtl.PlayerCount).First();
                        target = ptl.Location;
                        bestName = ptl.Name;
                    }
                }
                else
                    target = new Location(159, 101);
            }

            CalculateMovement(client, target, 0.5f);

            if(client.PlayerData.Pos.DistanceTo(target) < 1f && portals.Count != 0)
            {
                if (client.State.LastRealm?.Name.Contains(bestName) ?? false)
                {
                    Log("Last realm is still the best realm. Sending reconnect.");
                    if (client.ConnectTo(client.State.LastRealm))
                    {
                        gotoRealm = false;
                        return;
                    }
                }

                Log("Attempting connection.");
                gotoRealm = false;
                AttemptConnection(client, portals.OrderBy(ptl => ptl.Location.DistanceSquaredTo(client.PlayerData.Pos)).First().ObjectId);
            }
            await Task.Delay(5);
            if (gotoRealm)
            {
                MoveToRealms(client);
            } else
            {
                Log("Stopped moving to realm.");
            }
        }

        private async void AttemptConnection(Client client, int portalId)
        {
            UsePortalPacket packet = (UsePortalPacket)Packet.Create(PacketType.USEPORTAL);
            packet.ObjectId = portalId;

            if(!portals.Exists(ptl => ptl.ObjectId == portalId))
            {
                gotoRealm = true;
                MoveToRealms(client);
                return;
            }

            var pCount = portals.Find(p => p.ObjectId == portalId).PlayerCount;
            if (client.Connected && pCount < 85)
                client.SendToServer(packet);
            await Task.Delay(TimeSpan.FromSeconds(0.2));
            if (client.Connected && enabled)
                AttemptConnection(client, portalId);
            else if (enabled)
                Log("Connection successful.");
            else
                Log("Bot disabled, cancelling connection attempt.");
        }

        private void CalculateMovement(Client client, Location targetPosition, float tolerance)
        {
            // Left or right
            if (client.PlayerData.Pos.X < targetPosition.X - tolerance)
            {
                // Move right
                D_PRESSED = true;
                A_PRESSED = false;
            }
            else if (client.PlayerData.Pos.X <= targetPosition.X + tolerance)
            {
                // Stop moving
                D_PRESSED = false;
            }
            if (client.PlayerData.Pos.X > targetPosition.X + tolerance)
            {
                // Move left
                A_PRESSED = true;
                D_PRESSED = false;
            }
            else if (client.PlayerData.Pos.X >= targetPosition.X - tolerance)
            {
                // Stop moving
                A_PRESSED = false;
            }

            // Up or down
            if (client.PlayerData.Pos.Y < targetPosition.Y - tolerance)
            {
                // Move down
                S_PRESSED = true;
                W_PRESSED = false;
            }
            else if (client.PlayerData.Pos.Y <= targetPosition.Y + tolerance)
            {
                // Stop moving
                S_PRESSED = false;
            }
            if (client.PlayerData.Pos.Y > targetPosition.Y + tolerance)
            {
                // Move up
                W_PRESSED = true;
                S_PRESSED = false;
            }
            else if (client.PlayerData.Pos.Y >= targetPosition.Y - tolerance)
            {
                // Stop moving
                W_PRESSED = false;
            }
        }
    }
}
