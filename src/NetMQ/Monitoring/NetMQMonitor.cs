﻿using System;
using System.Threading;
using AsyncIO;
using JetBrains.Annotations;
using NetMQ.Core;

namespace NetMQ.Monitoring
{
    /// <summary>
    /// Monitors a <see cref="NetMQSocket"/> for events, raising them via events.
    /// </summary>
    public class NetMQMonitor : IDisposable
    {
        private readonly NetMQSocket m_monitoringSocket;
        private readonly bool m_ownsMonitoringSocket;
        private Poller m_attachedPoller;
        private int m_cancel;

        private readonly ManualResetEvent m_isStoppedEvent = new ManualResetEvent(true);

        public NetMQMonitor([NotNull] NetMQContext context, [NotNull] NetMQSocket monitoredSocket, [NotNull] string endpoint, SocketEvent eventsToMonitor)
        {
            Endpoint = endpoint;
            Timeout = TimeSpan.FromSeconds(0.5);

            monitoredSocket.Monitor(endpoint, eventsToMonitor);

            m_monitoringSocket = context.CreatePairSocket();
            m_monitoringSocket.Options.Linger = TimeSpan.Zero;

            m_monitoringSocket.ReceiveReady += Handle;

            m_ownsMonitoringSocket = true;
        }

        /// <summary>
        /// Initialises a monitor on <paramref name="socket"/> for a specified <paramref name="endpoint"/>.
        /// </summary>
        /// <remarks>
        /// This constructor matches the signature used by clrzmq.
        /// </remarks>
        /// <param name="socket">The socket to monitor.</param>
        /// <param name="endpoint">a string denoting the endpoint which will be the monitoring address</param>
        public NetMQMonitor([NotNull] NetMQSocket socket, [NotNull] string endpoint)
        {
            Endpoint = endpoint;
            Timeout = TimeSpan.FromSeconds(0.5);
            m_monitoringSocket = socket;

            m_monitoringSocket.ReceiveReady += Handle;

            m_ownsMonitoringSocket = false;
        }

        /// <summary>
        /// The monitoring address.
        /// </summary>
        public string Endpoint { get; private set; }

        /// <summary>
        /// Get whether this monitor is currently running.
        /// </summary>
        /// <remarks>
        /// This is set within <see cref="Start"/> and AttachToPoller, and cleared within DetachFromPoller.
        /// </remarks>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// How much time to wait on each poll iteration, the higher the number the longer it will take the poller to stop
        /// </summary>
        public TimeSpan Timeout { get; set; }

        #region Events

        /// <summary>
        /// Occurs when a connection is made to a socket.
        /// </summary>
        public event EventHandler<NetMQMonitorSocketEventArgs> Connected;

        /// <summary>
        /// Occurs when a synchronous connection attempt failed, and its completion is being polled for.
        /// </summary>
        public event EventHandler<NetMQMonitorErrorEventArgs> ConnectDelayed;

        /// <summary>
        /// Occurs when an asynchronous connect / reconnection attempt is being handled by a reconnect timer.
        /// </summary>
        public event EventHandler<NetMQMonitorIntervalEventArgs> ConnectRetried;

        /// <summary>
        /// Occurs when a socket is bound to an address and is ready to accept connections.
        /// </summary>
        public event EventHandler<NetMQMonitorSocketEventArgs> Listening;

        /// <summary>
        /// Occurs when a socket could not bind to an address.
        /// </summary>
        public event EventHandler<NetMQMonitorErrorEventArgs> BindFailed;

        /// <summary>
        /// Occurs when a connection from a remote peer has been established with a socket's listen address.
        /// </summary>
        public event EventHandler<NetMQMonitorSocketEventArgs> Accepted;

        /// <summary>
        /// Occurs when a connection attempt to a socket's bound address fails.
        /// </summary>
        public event EventHandler<NetMQMonitorErrorEventArgs> AcceptFailed;

        /// <summary>
        /// Occurs when a connection was closed.
        /// </summary>
        public event EventHandler<NetMQMonitorSocketEventArgs> Closed;

        /// <summary>
        /// Occurs when a connection couldn't be closed.
        /// </summary>
        public event EventHandler<NetMQMonitorErrorEventArgs> CloseFailed;

        /// <summary>
        /// Occurs when the stream engine (TCP and IPC specific) detects a corrupted / broken session.
        /// </summary>
        public event EventHandler<NetMQMonitorSocketEventArgs> Disconnected;

        #endregion

        private void Handle(object sender, NetMQSocketEventArgs socketEventArgs)
        {
            var monitorEvent = MonitorEvent.Read(m_monitoringSocket.SocketHandle);

            switch (monitorEvent.Event)
            {
                case SocketEvent.Connected:
                    InvokeEvent(Connected, new NetMQMonitorSocketEventArgs(this, monitorEvent.Addr, (AsyncSocket)monitorEvent.Arg));
                    break;
                case SocketEvent.ConnectDelayed:
                    InvokeEvent(ConnectDelayed, new NetMQMonitorErrorEventArgs(this, monitorEvent.Addr, (ErrorCode)monitorEvent.Arg));
                    break;
                case SocketEvent.ConnectRetried:
                    InvokeEvent(ConnectRetried, new NetMQMonitorIntervalEventArgs(this, monitorEvent.Addr, (int)monitorEvent.Arg));
                    break;
                case SocketEvent.Listening:
                    InvokeEvent(Listening, new NetMQMonitorSocketEventArgs(this, monitorEvent.Addr, (AsyncSocket)monitorEvent.Arg));
                    break;
                case SocketEvent.BindFailed:
                    InvokeEvent(BindFailed, new NetMQMonitorErrorEventArgs(this, monitorEvent.Addr, (ErrorCode)monitorEvent.Arg));
                    break;
                case SocketEvent.Accepted:
                    InvokeEvent(Accepted, new NetMQMonitorSocketEventArgs(this, monitorEvent.Addr, (AsyncSocket)monitorEvent.Arg));
                    break;
                case SocketEvent.AcceptFailed:
                    InvokeEvent(AcceptFailed, new NetMQMonitorErrorEventArgs(this, monitorEvent.Addr, (ErrorCode)monitorEvent.Arg));
                    break;
                case SocketEvent.Closed:
                    InvokeEvent(Closed, new NetMQMonitorSocketEventArgs(this, monitorEvent.Addr, (AsyncSocket)monitorEvent.Arg));
                    break;
                case SocketEvent.CloseFailed:
                    InvokeEvent(CloseFailed, new NetMQMonitorErrorEventArgs(this, monitorEvent.Addr, (ErrorCode)monitorEvent.Arg));
                    break;
                case SocketEvent.Disconnected:
                    InvokeEvent(Disconnected, new NetMQMonitorSocketEventArgs(this, monitorEvent.Addr, (AsyncSocket)monitorEvent.Arg));
                    break;
                default:
                    throw new Exception("unknown event " + monitorEvent.Event);
            }
        }

        private void InvokeEvent<T>(EventHandler<T> handler, T args) where T : NetMQMonitorEventArgs
        {
            var temp = handler;
            if (temp != null)
            {
                temp(this, args);
            }
        }

        private void InternalStart()
        {
            m_isStoppedEvent.Reset();
            IsRunning = true;
            m_monitoringSocket.Connect(Endpoint);
        }

        private void InternalClose()
        {
            try
            {
                m_monitoringSocket.Disconnect(Endpoint);
            }
            catch (Exception)
            {}
            finally
            {
                IsRunning = false;
                m_isStoppedEvent.Set();
            }
        }

        public void AttachToPoller([NotNull] Poller poller)
        {
            InternalStart();
            m_attachedPoller = poller;
            poller.AddSocket(m_monitoringSocket);
        }

        public void DetachFromPoller()
        {
            m_attachedPoller.RemoveSocket(m_monitoringSocket);
            m_attachedPoller = null;
            InternalClose();
        }

        /// <summary>
        /// Start monitor the socket, the method doesn't start a new thread and will block until the monitor poll is stopped
        /// </summary>
        /// <exception cref="InvalidOperationException">The Monitor must not have already started nor attached to a poller.</exception>
        public void Start()
        {
            // in case the sockets is created in another thread
            Thread.MemoryBarrier();

            if (IsRunning)
                throw new InvalidOperationException("Monitor already started");

            if (m_attachedPoller != null)
                throw new InvalidOperationException("Monitor attached to a poller");

            InternalStart();

            try
            {
                while (m_cancel == 0)
                {
                    m_monitoringSocket.Poll(Timeout);
                }
            }
            finally
            {
                InternalClose();
            }
        }

        /// <summary>
        /// Stop monitoring. Blocks until monitoring completed.
        /// </summary>
        /// <exception cref="InvalidOperationException">If this monitor is attached to a poller you must detach it first and not use the stop method.</exception>
        public void Stop()
        {
            if (m_attachedPoller != null)
                throw new InvalidOperationException("Monitor attached to a poller, please detach from poller and don't use the stop method");

            Interlocked.Exchange(ref m_cancel, 1);
            m_isStoppedEvent.WaitOne();
        }

        #region Dispose

        /// <summary>
        /// Release and dispose of any contained resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Release and dispose of any contained resources.
        /// </summary>
        /// <param name="disposing">true if releasing managed resources</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            if (m_attachedPoller != null)
            {
                DetachFromPoller();
            }
            else if (!m_isStoppedEvent.WaitOne(0))
            {
                Stop();
            }

            m_isStoppedEvent.Close();

            if (m_ownsMonitoringSocket)
            {
                m_monitoringSocket.Dispose();
            }
        }

        #endregion
    }
}
