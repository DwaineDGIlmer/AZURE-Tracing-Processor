using Azure.Diagnostics.Tracing;
using Microsoft.ServiceBus.Messaging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Azure.Diagnostics
{
    class Program
    {
        static void Main(string[] args)
        {
            AzureTraceListener newWriter = new AzureTraceListener("CriticalApplication", "traceevents", true);
            newWriter.WriteLine("Nothing new");
        }
    }
}

namespace Azure.Diagnostics.Tracing
{
    public sealed class AzureTraceListener : TraceListener, IDisposable
    {
        #region private
        private bool _disposed;
        private bool _isInitialized;
        private bool _isRunning;
        private string _eventSourceName;
        private EventHubClient _hubClient;
        private ManualResetEvent _flowController;
        #endregion

        #region private statics
        private static int _minimumSize;
        private static int _maximumSize;
        private static char[] _trimChars = new char[] { ' ', '\0', '\n', '\r' };
        #endregion

        #region public properties statics
        public bool IsRunning { get { return _isRunning; } }
        public bool IsInitalized { get { return _isInitialized; } }
        public string PartitionKey { get; set; }
        #endregion

        #region public properties
        public string EventSourceName { get { return _eventSourceName; } set { _eventSourceName = string.IsNullOrEmpty(_eventSourceName) ? ProviderName : value; } }
        public string ProviderName;
        public Guid ProviderId;
        #endregion

        #region  Destructor
        ~AzureTraceListener()
        {
            this.Dispose(false);
        }
        #endregion

        #region  Constructor
        static AzureTraceListener()
        {
            _trimChars = new char[] { ' ', '\0', '\n', '\r' };
            _minimumSize = 200;
            _maximumSize = 250000;
            // Max size for EventData on the service bus is 256k
        }
        public AzureTraceListener(string sourcename, string eventHubname = "", bool autoStart = true)
        {
            // Set up the object for use
            ProviderName = "AzureTraceListener";
            ProviderId = new Guid("00000000-1111-2222-3333-444444444444");
            PartitionKey = "0"; 

            // Get the source name
            _eventSourceName = (string.IsNullOrEmpty(sourcename)) ? ProviderName : sourcename;

            // Check to see if we were already added as a Trace provider
            if (!Trace.Listeners.Contains(this))
            {
                Trace.Listeners.Add(this);
            }

            // Initialize the Servicebus client
            if(!InitializeHubClient(eventHubname))
            {
                // This is fatal, it is better to fail early and throw an Exception than to construct an object that is not functional
                // https://msdn.microsoft.com/en-us/library/aa269568(v=vs.60).aspx
                throw (new ArgumentException("Invalid input"));
            }

            // Allocate the eventing mechanism 
            _flowController = new ManualResetEvent(false);

            // Start up the listener
            if (autoStart)
            {
                this.Start();
            }
        }
        private bool InitializeHubClient(string eventHubname)
        {
            if (_isInitialized)
            {
                return true;
            }

            string hubname = (string.IsNullOrEmpty(eventHubname)) ? ConfigurationManager.AppSettings["Microsoft.Eventhub.Name"] : eventHubname;
            if (!string.IsNullOrEmpty(hubname))
            {
                // Creates a new instance of the EventHubClient instance, using a connection string from the application configuration settings
                if ((_hubClient = EventHubClient.Create(hubname)) != null)
                {
                    return _isInitialized = true;
                }
            }

            // Failed condition
            return false;
        }
        #endregion

        #region Dispose pattern
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            { return; }

            // flush out the events in the pipeline
            // NOTE: You should wait for events to draing before closing the client
            this.Flush();

            if (this._hubClient != null)
            {
                this._hubClient.CloseAsync();
                this._hubClient = null;
            }

            if (this._flowController != null)
            {
                this._flowController.Close();
                this._flowController.Dispose();
                this._flowController = null;
            }

            this._isInitialized = false;
            this._disposed = true;

            // Call dispose on the base class
            base.Dispose(disposing);
        }
        #endregion

        #region External controls
        public void Stop()
        {
            _isRunning = false;
            _flowController.Reset();
        }
        public override void Flush()
        {
            if (!_isRunning)
            {
                return;
            }
            this.Flush();
        }
        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            // Start up all ofthe clients
            _flowController.Set();

            // Set the bit
            _isRunning = true;
        }
        public void Wait()
        {
            _flowController.WaitOne();
        }
        #endregion

        #region Tracelistener write methods
        public override void WriteLine(string message)
        {
            if (_isInitialized && _isRunning)
            {
                this.SendAsync(message, "0", "TraceEvent");
            }
        }
        public override void Write(string message)
        {
            if (_isInitialized && _isRunning)
            {
                this.SendAsync(message, "0", "TraceEvent");
            }
        }
        #endregion

        #region The Event send handler
        private void SendAsync(string message, string eventId, string eventType)
        {
            if (_isInitialized && _isRunning)
            {
                HubData newData = new HubData();
                newData.Eventname = string.Format("{0}_{1}", eventType, DateTime.Now.Ticks);
                newData.EventId = eventId;
                newData.EventMessage = message.TrimEnd(_trimChars);
                newData.EventproviderId = ProviderId.ToString();
                newData.Eventprovider = ProviderName;
                newData.EventSource = EventSourceName;
                newData.Senttimestamp = newData.Eventtimestamp = DateTime.Now.ToUniversalTime();
                newData.Eventtype = eventType;

                this.SendAsync(newData);
            }
        }
        private void SendAsync(HubData newData)
        {
            // Create the event data aand serialize the class to a Jason object
            var serializedString = JsonConvert.SerializeObject(newData);

            // Create an  EventData from the Jason object
            EventData data = new EventData(Encoding.UTF8.GetBytes(serializedString))
            {
                PartitionKey = this.PartitionKey
            };

            // If the serialized data is too small return
            if (data.SerializedSizeInBytes > _minimumSize &&
                data.SerializedSizeInBytes <= _maximumSize)
            {
                _hubClient.SendAsync(data);
            }
        }
        #endregion
    }

    public class HubData
    { 
        public string Eventname { get; set; }
        public string Eventprovider { get; set; }
        public string EventproviderId { get; set; }
        public string EventSource { get; set; }
        public string EventId { get; set; }
        public string Eventtype { get; set; }
        public string EventMessage { get; set; }
        public DateTime Eventtimestamp { get; set; }
        public DateTime Senttimestamp { get; set; }
    }
}