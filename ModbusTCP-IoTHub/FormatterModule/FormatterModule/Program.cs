namespace FormatterModule
{
    using System;
    using System.IO;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Client.Transport.Mqtt;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;

    class Program
    {
        static int counter;

        static void Main(string[] args)
        {
            // The Edge runtime gives us the connection string we need -- it is injected as an environment variable
            string connectionString = Environment.GetEnvironmentVariable("EdgeHubConnectionString");

            // Cert verification is not yet fully functional when using Windows OS for the container
            bool bypassCertVerification = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (!bypassCertVerification) InstallCert();
            Init(connectionString, bypassCertVerification).Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Add certificate in local cert store for use by client for secure connection to IoT Edge runtime
        /// </summary>
        static void InstallCert()
        {
            string certPath = Environment.GetEnvironmentVariable("EdgeModuleCACertificateFile");
            if (string.IsNullOrWhiteSpace(certPath))
            {
                // We cannot proceed further without a proper cert file
                Console.WriteLine($"Missing path to certificate collection file: {certPath}");
                throw new InvalidOperationException("Missing path to certificate file.");
            }
            else if (!File.Exists(certPath))
            {
                // We cannot proceed further without a proper cert file
                Console.WriteLine($"Missing path to certificate collection file: {certPath}");
                throw new InvalidOperationException("Missing certificate file.");
            }
            X509Store store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(new X509Certificate2(X509Certificate2.CreateFromCertFile(certPath)));
            Console.WriteLine("Added Cert: " + certPath);
            store.Close();
        }


        /// <summary>
        /// Initializes the DeviceClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init(string connectionString, bool bypassCertVerification = false)
        {
            Console.WriteLine("Connection String {0}", connectionString);

            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            // During dev you might want to bypass the cert verification. It is highly recommended to verify certs systematically in production
            if (bypassCertVerification)
            {
                mqttSetting.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
            }
            ITransportSettings[] settings = { mqttSetting };

            // Open a connection to the Edge runtime
            DeviceClient ioTHubModuleClient = DeviceClient.CreateFromConnectionString(connectionString, settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            // Register callback to be called when a message is received by the module
            await ioTHubModuleClient.SetInputMessageHandlerAsync("input1", MessageReceiver, ioTHubModuleClient);
        }

        static async Task<MessageResponse> MessageReceiver(Message message, object userContext)
        {
            int counterValue = Interlocked.Increment(ref counter);

            var deviceClient = userContext as DeviceClient;
            if (deviceClient == null)
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            byte[] messageBytes = message.GetBytes();
            string messageString = Encoding.UTF8.GetString(messageBytes);
            Console.WriteLine($"Received message: {counterValue}, Body: {messageString}");

            messageString = "{\"body\":" + messageString + "}";
            var data = JsonConvert.DeserializeObject<RequestModel>(messageString);
            var response = CreateResponse(data);
            messageString = JsonConvert.SerializeObject(response);
            messageBytes = Encoding.ASCII.GetBytes(messageString);


            if (!string.IsNullOrEmpty(messageString))
            {
                var pipeMessage = new Message(messageBytes);
                await deviceClient.SendEventAsync("output1", pipeMessage);
                Console.WriteLine("Received message sent");
            }
            return MessageResponse.Completed;
        }

        static ResponseModel CreateResponse(RequestModel data)
        {
            ResponseModel response = new ResponseModel();
            var hwIds = data.body.Select(d => d.HwId).Distinct().ToList();
            foreach (var element in hwIds)
            {
                var dataElements = data.body.Where(d => d.HwId.Equals(element)).ToList();
                response.DeviceData.Add(GenerateModel(dataElements));
            }
            return response;
        }

        static Devicedata GenerateModel(List<Body> data)
        {
            Devicedata response = new Devicedata();

            response.AMPSAvg = GetFloatValue(data.Where(d => d.DisplayName.Equals("Amps System Avg")).ToList(), "Amps System Avg");
            response.AMPSL1 = GetFloatValue(data.Where(d => d.DisplayName.Equals("Amps L1")).ToList(), "Amps L1");
            response.AMPSL2 = GetFloatValue(data.Where(d => d.DisplayName.Equals("Amps L2")).ToList(), "Amps L2");
            response.AMPSL3 = GetFloatValue(data.Where(d => d.DisplayName.Equals("Amps L3")).ToList(), "Amps L3");
            response.Device_Id = data.FirstOrDefault().HwId;
            response.kWL1 = GetFloatValue(data.Where(d => d.DisplayName.Equals("kW L1")).ToList(), "kW L1");
            response.kWL2 = GetFloatValue(data.Where(d => d.DisplayName.Equals("kW L2")).ToList(), "kW L2");
            response.kWL3 = GetFloatValue(data.Where(d => d.DisplayName.Equals("kW L3")).ToList(), "kW L3");
            response.kWSystem = GetFloatValue(data.Where(d => d.DisplayName.Equals("kW System")).ToList(), "kW System");
            response.VoltsL1toNeutral = GetFloatValue(data.Where(d => d.DisplayName.Equals("Volts L1 to Neutral")).ToList(), "Volts L1 to Neutral");
            response.VoltsL2toNeutral = GetFloatValue(data.Where(d => d.DisplayName.Equals("Volts L2 to Neutral")).ToList(), "Volts L2 to Neutral");
            response.VoltsL3toNeutral = GetFloatValue(data.Where(d => d.DisplayName.Equals("Volts L3 to Neutral")).ToList(), "Volts L3 to Neutral");
            Console.WriteLine();
            Console.ReadLine();
            return response;
        }

        static float GetFloatValue(List<Body> data, string parameter)
        {
            var hexValue = "";
            var dataList = data.Where(d => d.DisplayName.Equals(parameter)).ToList();
            if (dataList.Count > 1)
            {
                dataList.Take(2).ToList().ForEach(d => hexValue += int.Parse(d.Value).ToString("X"));
            }
            if (hexValue == "" || string.IsNullOrEmpty(hexValue))
                hexValue = "00000000";
            var byteValue = BitConverter.GetBytes(uint.Parse(hexValue, System.Globalization.NumberStyles.AllowHexSpecifier));
            return BitConverter.ToSingle(byteValue, 0);
        }


        public class RequestModel
        {
            public Body[] body { get; set; }
        }

        public class Body
        {
            public string DisplayName { get; set; }
            public string HwId { get; set; }
            public string Address { get; set; }
            public string Value { get; set; }
            public string SourceTimestamp { get; set; }
        }


        public class ResponseModel
        {
            public List<Devicedata> DeviceData { get; set; }

            public ResponseModel()
            {
                if (DeviceData == null)
                    DeviceData = new List<Devicedata>();
            }

        }

        public class Devicedata
        {
            public string Device_Id { get; set; }
            public double AMPSL1 { get; set; }
            public double AMPSL2 { get; set; }
            public double AMPSL3 { get; set; }
            public double AMPSAvg { get; set; }
            public double VoltsL1toNeutral { get; set; }
            public double VoltsL2toNeutral { get; set; }
            public double VoltsL3toNeutral { get; set; }
            public double kWL1 { get; set; }
            public double kWL2 { get; set; }
            public double kWL3 { get; set; }
            public double kWSystem { get; set; }
        }
    }
}
