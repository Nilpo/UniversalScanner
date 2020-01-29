﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UniversalScanner
{
    public delegate void scan();
    public interface ScannerViewer
    {
        void deviceFound(string protocol, string deviceIP, string deviceType, string serial, int color);
        event scan scanEvent;
    }


    public abstract class ScanEngine : IDisposable
    {
        protected IPAddress multicastIP;
        protected int multicastPort = 0;

        protected Thread scannerThread = null;
        protected int scannerPort = 0;

        protected ScannerViewer viewer = null;

        protected bool closing = false;
        public bool isDisposed = false;

        [DllImport("wsock32.dll")]
        private static extern UInt32 ntohl(UInt32 netshort);
        [DllImport("wsock32.dll")]
        private static extern UInt32 htonl(UInt32 netshort);

        protected struct networkBundle
        {
            public bool inUse;
            public Thread thread;
            public UdpClient udp;
            public IPEndPoint endPoint;
        };

        protected networkBundle globalListener;
        protected networkBundle multicastListener;
        protected networkBundle[] interfacesListerner;

        public ScanEngine()
        {
            globalListener.inUse = false;
            multicastListener.inUse = false;
        }

        public abstract void scan();

        private void debugWriteText(string text)
        {
#if DEBUG
            string[] lines;
            Thread thread;
            string splitter;
            StringBuilder result;
            Regex isBinary;

            isBinary = new Regex("[^\x20-\x7E\t\r\n]");
            thread = Thread.CurrentThread;
            splitter = new string('-', 16);
            result = new StringBuilder(splitter);

            if (isBinary.IsMatch(text))
            {

                for (int i=0; i < text.Length; i++)
                {
                    if (i % 16 == 0)
                    {
                        result.AppendFormat("\n[{0,4}] ", thread.ManagedThreadId);
                    }
                    result.AppendFormat(" {0:X02}", (byte)text[i]);
                }
            }
            else
            {
                lines = Regex.Split(text, "\r\n|\r|\n");

                foreach (string line in lines)
                {
                    result.AppendFormat("\n[{0,4}] {1}", thread.ManagedThreadId, line);
                }

            }
            result.Append("\n").Append(splitter);

            Debug.WriteLine(result.ToString());
#endif
        }

        public int listenUdpGlobal(int localPort=0)
        {
            if (localPort != 0)
            {
                if (!isFreeUdpPort(localPort))
                {
                    Trace.WriteLine(String.Format("Error: ScanEngine.listenUdpGlobal(): Local UDP port {0} is already in use...", localPort));
                    return -1;
                }
            }
            else
            {
                localPort = getFreeUdpPort();
            }

            try
            {
                Trace.WriteLine(String.Format("Listening on UDP {0}:{1}...", IPAddress.Any.ToString(), localPort));

                /* configure UdpClient and EndPoint */
                globalListener.udp = new UdpClient();
                globalListener.udp.EnableBroadcast = true;
                globalListener.endPoint = new IPEndPoint(IPAddress.Any, localPort);

                // start unicast reciever on main interface
                globalListener.thread = new Thread(unicastReciever);
                globalListener.thread.IsBackground = true;

                globalListener.inUse = true;
            }
            catch
            {
                Trace.WriteLine("Error: ScanEngine.listenUdpGlobal(): Listening failure!");
                globalListener.inUse = false;
                return -1;
            }
            globalListener.thread.Start();
            
            return localPort;
        }

        public void listenUdpInterfaces()
        {
            IPAddress[] addresses;
            int len;

            addresses = listActiveAddresses(AddressFamily.InterNetwork);
            len = addresses.Length;

            interfacesListerner = new networkBundle[len];

            for (int i = 0; i < len; i++)
            {
                int localPort = getFreeUdpPort();
                IPAddress address = addresses[i];
                try
                {
                    Trace.WriteLine(String.Format("Listening on UDP {0}:{1}...", address.ToString(), localPort));

                    // configure UdpClient and EndPoint
                    interfacesListerner[i].udp = new UdpClient();
                    interfacesListerner[i].udp.EnableBroadcast = true;
                    interfacesListerner[i].endPoint = new IPEndPoint(address, localPort);

                    // start unicast reciever on main interface
                    interfacesListerner[i].thread = new Thread(unicastReciever);
                    interfacesListerner[i].thread.IsBackground = true;

                    interfacesListerner[i].inUse = true;
                }
                catch
                {
                    Trace.WriteLine(String.Format("Error: ScanEngine.listenUdpInterfaces(): Listening failure on UDP {0}:{1}!", address.ToString(), localPort));
                    interfacesListerner[i].inUse = false;
                }
                interfacesListerner[i].thread.Start();
            }

            return;
        }

        public bool listenMulticast(IPAddress multicastIP, int multicastPort)
        {
            this.multicastIP = multicastIP;
            this.multicastPort = multicastPort;

            try
            {
                Trace.WriteLine(String.Format("Subscribing to multicast {0}:{1}...", multicastIP, multicastPort));

                multicastListener.inUse = true;
                multicastListener.thread = new Thread(multicastReciever);
                multicastListener.thread.IsBackground = true;
                multicastListener.thread.Start();
            }
            catch
            {
                Trace.WriteLine(String.Format("Error: ScanEngine.listenMulticast(): Subscriptiong failure to multicast {0}:{1}!", multicastIP, multicastPort));
                multicastListener.inUse = false;
                return false;
            }

            return true;
        }

        public abstract byte[] sender(IPEndPoint dest);
        public abstract void reciever(IPEndPoint from, byte[] data);

        public void registerViewer(ScannerViewer viewer)
        {
            this.viewer = viewer;
        }

        public bool send(IPEndPoint endpoint)
        {
            byte[] data;

            if (interfacesListerner == null)
            {
                Trace.WriteLine("Error: ScanEngine.send(): no interface-distributed sockets, you must call listenUdpInterfaces() before!");
                return false;
            }

            data = sender(endpoint);
            foreach (networkBundle net in interfacesListerner)
            {
                if (net.inUse)
                {
#if DEBUG
                    Debug.WriteLine(String.Format("Sending from interface {0} to {1}...", net.endPoint.Address.ToString(), endpoint.ToString()));
                    debugWriteText(Encoding.UTF8.GetString(data));
#endif
                    try
                    {
                        net.udp.Send(data, data.Length, endpoint);
                    }
                    catch
                    {
                        Trace.WriteLine("Error: ScanEngine.send(): Unable to send the packet over the network!");
                    }
                }
            }
            return true;
        }

        public bool sendBroadcast(int port)
        {
            return send(new IPEndPoint(IPAddress.Broadcast, port));
        }

        public bool sendMulticast(IPAddress dest, int port)
        {
            return send(new IPEndPoint(dest, port));
        }

        public bool sendUnicast(IPAddress dest, int port)
        {
            return send(new IPEndPoint(dest, port));
        }

        public bool sendNetScan(int port)
        {
            if (scannerThread != null)
            {
                scannerThread.Abort();
            }

            if (interfacesListerner == null)
            {
                Trace.WriteLine("Error: ScanEngine.send(): no interface-distributed sockets, you must call listenUdpInterfaces() before!");
                return false;
            }
            scannerPort = port;

            scannerThread = new Thread(sendNetScanner);
            scannerThread.IsBackground = true;
            scannerThread.Start();

            return true;
        }

        private void sendNetScanner()
        {
            byte[] data;

            foreach (networkBundle net in interfacesListerner)
            {
                if (net.inUse && isPrivateIPv4Network(net.endPoint.Address))
                {
                    IPAddress mask = getMaskOfAddressIPv4(net.endPoint.Address);
                    IPAddress[] subNetAddresses;

                    subNetAddresses = subNetListIPv4Addresses(net.endPoint.Address, mask, 254);

                    foreach (IPAddress local in subNetAddresses)
                    {
                        if (local.Equals(net.endPoint.Address))
                            continue;

                        IPEndPoint endpoint = new IPEndPoint(local, scannerPort);
                        data = sender(endpoint);
#if DEBUG
                        Debug.WriteLine(String.Format("Sending from interface {0} to {1}...", net.endPoint.Address.ToString(), endpoint.ToString()));
                        debugWriteText(Encoding.UTF8.GetString(data));
#endif

                        try
                        {
                            net.udp.Send(data, data.Length, endpoint);
                        }
                        catch
                        {
                            Trace.WriteLine(String.Format("Error: ScanEngine.sendNetScanner(): error sending packet to {0}", endpoint.ToString()));
                        }
                    }
                }
            }
        }


        protected bool isFreeUdpPort(int localPort)
        {
            IEnumerable<int> portsInUse;

            portsInUse =
                from used in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                where used.Port == localPort
                select used.Port;

            return (portsInUse == null);
        }

        protected int getFreeUdpPort()
        {
            int[] portRange = { 1024, 65534 };
            IEnumerable<int> portsUseable, portsInUse, portsFree;
            int countFree;
            Random rand;

            portsUseable = Enumerable.Range(portRange[0], portRange[1] - portRange[0]);
            portsInUse =
                from p in portsUseable
                join used in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
            on p equals used.Port
                select p;

            portsFree = portsUseable.Except(portsInUse);

            countFree = portsFree.Count();
            if (countFree == 0)
            {
                Trace.WriteLine("Error: ScanEngine.UdpFreePortProvider(): No free UDP port!");
                throw new System.OverflowException();
            }

            rand = new Random();
            int index = rand.Next(0, countFree);

            return portsFree.ElementAt(index);
        }

        protected IPAddress[] listActiveAddresses(AddressFamily adressType)
        {
            var address_list =
                from iface in NetworkInterface.GetAllNetworkInterfaces()
                where iface.OperationalStatus == OperationalStatus.Up
                select iface.GetIPProperties() into ifaceProp
                from addr in ifaceProp.UnicastAddresses
                where (addr.Address.AddressFamily == adressType)
                select addr.Address;

            return address_list.Cast<IPAddress>().ToArray();
        }

        protected IPAddress getMaskOfAddressIPv4(IPAddress address)
        {
            var masks= (from iface in NetworkInterface.GetAllNetworkInterfaces()
                where iface.OperationalStatus == OperationalStatus.Up
                select iface.GetIPProperties() into ifaceProp
                from addr in ifaceProp.UnicastAddresses
                where (addr.Address.AddressFamily == AddressFamily.InterNetwork && addr.Address.Equals(address))
                select addr.IPv4Mask);

            if (masks.Count() == 1)
            {
                return masks.First();
            }

            return IPAddress.Parse("255.255.255.255");
        }

        protected IPAddress[] subNetListIPv4Addresses(IPAddress address, IPAddress subNetMask, UInt32 maxLen)
        {
            UInt32 addr, mask, first, last, len, i, current;
            IPAddress[] result;

            addr = ntohl(BitConverter.ToUInt32(address.GetAddressBytes(), 0));
            mask = ntohl(BitConverter.ToUInt32(subNetMask.GetAddressBytes(), 0));

            first = (addr & mask) + 1;
            last = (addr | ~mask) -1;

            len = last - first + 1;
            if (len > maxLen)
            {
                len = maxLen;
            }
            result = new IPAddress[len];
            for (i=0; i < len; i++)
            {
                current = first + i;
                
                result[i] = new IPAddress(
                BitConverter.ToUInt32(new byte[] { (byte)(current >> 24), (byte)(current >> 16), (byte)(current >> 8), (byte)current }, 0));
            }

            return result;
        }

        protected bool isPrivateIPv4Network(IPAddress address)
        {
            UInt32 addr, subNetPrivate, maskPrivate;

            addr = ntohl(BitConverter.ToUInt32(address.GetAddressBytes(), 0));

            subNetPrivate = 0xC0A80000;
            maskPrivate = 0xFFFF0000;
            if ((addr & maskPrivate) == subNetPrivate) return true;

            subNetPrivate = 0xAC100000;
            maskPrivate = 0xFFF00000;
            if ((addr & maskPrivate) == subNetPrivate) return true;

            subNetPrivate = 0x0A000000;
            maskPrivate = 0xFF000000;
            if ((addr & maskPrivate) == subNetPrivate) return true;

            subNetPrivate = 0xA9FE0000;
            maskPrivate = 0xFFFF0000;
            if ((addr & maskPrivate) == subNetPrivate) return true;

            return false;
        }

        private void unicastReciever()
        {
            byte[] data;
            UdpClient unicastUDP;
            IPEndPoint unicastEP;

            if (Thread.CurrentThread == globalListener.thread)
            {
                unicastUDP = globalListener.udp;
                unicastEP = globalListener.endPoint;
            }
            else
            {
                unicastUDP =
                    (from thread in interfacesListerner
                     where thread.thread == Thread.CurrentThread
                     select thread.udp).First();
                unicastEP =
                    (from thread in interfacesListerner
                     where thread.thread == Thread.CurrentThread
                     select thread.endPoint).First();
            }

            try
            {
                unicastUDP.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                unicastUDP.Client.Bind(unicastEP);
            }
            catch (Exception)
            {
                Trace.WriteLine(String.Format("Error: ScanEngine.unicastReciever(): Unable to bind {0}!", unicastEP.ToString()));
                return;
            }

            while (!closing)
            {
                try
                {
                    data = unicastUDP.Receive(ref unicastEP);
#if DEBUG
                    Debug.WriteLine(String.Format("Recieved unicast from {0}.", unicastEP.ToString()));
                    debugWriteText(Encoding.UTF8.GetString(data));
#endif
                    reciever(unicastEP, data);
                }
                catch
                {
                    Trace.WriteLine("Error: ScanEngine.unicastReciever(): error while revieve unicast packet!");
                }
            }
        }

        /* multicast listener */
        private void multicastReciever()
        {
            byte[] data;

            multicastListener.udp = new UdpClient();
            multicastListener.endPoint = new IPEndPoint(IPAddress.Any, multicastPort);
            try
            {
                multicastListener.udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(multicastIP));
                multicastListener.udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                multicastListener.udp.Client.Bind(multicastListener.endPoint);
            }
            catch (Exception)
            {
                Trace.WriteLine(String.Format("Error: ScanEngine.multicastReciever(): Unable to bind {0}!", multicastListener.endPoint.ToString()));
                return;
            }

            while (!closing)
            {
                try
                {
                    data = multicastListener.udp.Receive(ref multicastListener.endPoint);
    #if DEBUG
                    Debug.WriteLine(String.Format("Recieved multicast from {0}.", multicastListener.endPoint.ToString()));
                    debugWriteText(Encoding.UTF8.GetString(data));
    #endif
                    reciever(multicastListener.endPoint, data);
                }
                catch
                {
                    Trace.WriteLine(String.Format("Error: ScanEngine.multicastReciever(): error while recieving multicast packet on {0}!", multicastListener.endPoint.ToString()));
                }
            }
            multicastListener.udp.Close();
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            closing = true;

            if (globalListener.inUse)
            {
                globalListener.thread.Abort();
            }
            if (multicastListener.inUse)
            {
                multicastListener.thread.Abort();
            }
            if (interfacesListerner != null)
            {
                foreach (networkBundle net in interfacesListerner)
                {
                    if (net.inUse)
                    {
                        net.thread.Abort();
                    }
                }
            }
            isDisposed = true;
        }

        ~ScanEngine()
        {
            this.Dispose();
        }
    }
}
