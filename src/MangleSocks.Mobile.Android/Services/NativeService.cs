﻿using System;
using System.Collections.ObjectModel;
using System.Net;
using Android.App;
using Android.Content;
using Android.OS;
using Java.Lang;
using MangleSocks.Core.Server;
using MangleSocks.Mobile.Bootstrap;
using MangleSocks.Mobile.Messaging;
using MangleSocks.Mobile.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Xamarin.Forms;
using Exception = System.Exception;
using FormsApplication = Xamarin.Forms.Application;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Log = Android.Util.Log;

namespace MangleSocks.Mobile.Droid.Services
{
    [Service(Name = "org.manglesocks.service")]
    public class NativeService : Service, INativeService
    {
        const int c_MaxLogMessages = 150;

        readonly AppSettingsModel _settings;
        readonly ObservableCollection<ServiceLogMessage> _logMessages;
        readonly object _startLocker = new object();

        Binder _binder;
        IServiceScope _serviceScope;
        bool _started;

        public ServiceStatus Status
        {
            get
            {
                lock (this._startLocker)
                {
                    return this._started ? ServiceStatus.Started : ServiceStatus.Stopped;
                }
            }
        }

        public NativeService()
        {
            this._settings = AppSettingsModel.LoadFrom(App.Settings);
            this._logMessages = new ObservableCollection<ServiceLogMessage>();
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            lock (this._startLocker)
            {
                if (!this._started)
                {
                    ILogger log = null;
                    try
                    {
                        MessagingCenter.Instance.Subscribe<FormsApplication, ServiceLogMessage>(
                            this,
                            nameof(ServiceLogMessage),
                            (_, logMessage) => Device.BeginInvokeOnMainThread(
                                () =>
                                {
                                    if (this._logMessages.Count > c_MaxLogMessages)
                                    {
                                        this._logMessages.RemoveAt(0);
                                    }

                                    this._logMessages.Add(logMessage);
                                }));

                        var listenEndPoint = new IPEndPoint(IPAddress.Loopback, this._settings.ListenPort);
                        var serviceProvider = this.CreateServiceProvider();

                        this._serviceScope = serviceProvider.CreateScope();
                        log = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(this.GetType().Name);

                        var socksServer = this._serviceScope.ServiceProvider.GetRequiredService<SocksServer>();
                        socksServer.Start();

                        var notification = new Notification.Builder(this)
                            .SetSubText(listenEndPoint.ToString())
                            .SetVisibility(NotificationVisibility.Secret)
                            .SetSmallIcon(Resource.Drawable.ic_stat_socks)
                            .SetContentIntent(
                                PendingIntent.GetActivity(
                                    this,
                                    0,
                                    new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ReorderToFront),
                                    PendingIntentFlags.UpdateCurrent))
                            .SetOngoing(true)
                            .Build();
                        this.StartForeground(1, notification);

                        this._started = true;
                        this.NotifyStatusUpdate();
                    }
                    catch (Exception ex)
                    {
                        if (log != null)
                        {
                            log.LogError(ex, "Failed to start service");
                        }
                        else
                        {
                            Log.Error(nameof(MangleSocks), Throwable.FromException(ex), "Failed to start service");
                        }

                        this.StopSelfResult(1);
                    }
                }

                return StartCommandResult.Sticky;
            }
        }

        IServiceProvider CreateServiceProvider()
        {
            return MobileServiceConfiguration.CreateServiceProvider(
                this._settings,
                config => config.WriteTo.AndroidLog(outputTemplate: "{Scope} {Message}{NewLine}{Exception}"));
        }

        public override IBinder OnBind(Intent intent)
        {
            this._binder = this._binder ?? new NativeServiceBinder(this);
            return this._binder;
        }

        public override void OnDestroy()
        {
            this.StopSocksServer();
            this.NotifyStatusUpdate();

            MessagingCenter.Instance.Unsubscribe<FormsApplication, ServiceLogMessage>(this, nameof(ServiceLogMessage));

            base.OnDestroy();
        }

        void StopSocksServer()
        {
            this._serviceScope?.Dispose();
            this._serviceScope = null;
            this._started = false;
        }

        public void NotifyStatusUpdate()
        {
            MessagingCenter.Instance.Send(
                FormsApplication.Current,
                nameof(ServiceStatusUpdate),
                new ServiceStatusUpdate
                {
                    Status = this.Status,
                    LogMessages = this._logMessages
                });
        }

        protected override void Dispose(bool disposing)
        {
            this.StopSocksServer();
            base.Dispose(disposing);
        }
    }
}