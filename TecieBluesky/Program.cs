using FishyFlip;
using FishyFlip.Events;
using FishyFlip.Models;
using FishyFlip.Tools;
using Microsoft.Extensions.Logging.Debug;
using Newtonsoft.Json;
using Org.BouncyCastle.Bcpg;
using System.IO.Pipes;
using System.Reflection;
using System.Text;

namespace TecieBluesky
{
    internal class Program
    {
        private static int numThreads = 4;
        static ATProtocol? atProtocol = null;

        public static Task Main(string[] args) => new Program().MainAsync();
        public async Task MainAsync()
        {
            var debugLog = new DebugLoggerProvider();
            var atProtocolBuilder = new ATProtocolBuilder()
                .EnableAutoRenewSession(true)
                .WithLogger(debugLog.CreateLogger("Tecie"));
            atProtocol = atProtocolBuilder.Build();

            Result<Session> result = await atProtocol.Server.CreateSessionAsync("tecie.thenergeticon.com", Environment.GetEnvironmentVariable("TEC_BSKY") ?? "NULL");
            result.Switch(
                success =>
                {
                    Console.WriteLine($"Session: {success.Did}");
                },
                error =>
                {
                    Console.WriteLine($"Error: {error.StatusCode} {error.Detail}");
                    return;
                });

            int i;
            Thread[]? servers = new Thread[numThreads];

            Console.WriteLine("\n*** Tecie Bluesky ***\n");
            Console.WriteLine("Server started, waiting for client connect...\n");
            for (i = 0; i < numThreads; i++)
            {
                servers[i] = new(ServerThread);
                servers[i]?.Start();
            }
            Thread.Sleep(250);
            while (i > 0)
            {
                for (int j = 0; j < numThreads; j++)
                {
                    if (servers[j] != null)
                    {
                        if (servers[j]!.Join(50))
                        {
                            Console.WriteLine($"Server thread[{servers[j]!.ManagedThreadId}] finished.");
                            servers[j] = new Thread(ServerThread);
                            servers[j]?.Start();
                        }
                    }
                }
            }
            Console.WriteLine("\nServer threads exhausted, exiting.");
        }

        private static void ServerThread()
        {
            NamedPipeServerStream pipeServer =
                new NamedPipeServerStream("TecieBlueskyPipe", PipeDirection.InOut, numThreads);

            int threadId = Thread.CurrentThread.ManagedThreadId;

            // Wait for a client to connect
            pipeServer.WaitForConnection();

            Console.WriteLine($"Client connected on thread[{threadId}].");
            try
            {
                // Read the request from the client. Once the client has
                // written to the pipe its security token will be available.

                StreamString ss = new StreamString(pipeServer);
                string authkey = Environment.GetEnvironmentVariable("TECKEY") ?? "no key found";

                // Verify our identity to the connected client using a
                // string that the client anticipates.
                if (ss.ReadString() != authkey) { ss.WriteString("Unauthorized client!"); throw new Exception("Unauthorized client connection attemted!"); }
                ss.WriteString(authkey);
                string operation = ss.ReadString(); // E for event ping  A for announcement  U for update

                string post = "";
                ss.WriteString("READY");
                string message = ss.ReadString();

                Task<Result<CreatePostResponse>>? postCreate = null;
                switch (operation)
                {
                    case "A":
                        post = message;
                        postCreate = atProtocol!.Repo.CreatePostAsync(post);
                        break;
                    case "E":
                        EventPingInfo eventinfo = JsonConvert.DeserializeObject<EventPingInfo>(message)!;
                        Console.WriteLine(JsonConvert.SerializeObject(eventinfo, Formatting.Indented));
                        post = $"An event is starting!";

                        ExternalEmbed embed = new(new External(null, eventinfo.EventName, eventinfo.EventDescription, "https://thenergeticon.com/Events/currentevent"), Constants.EmbedTypes.External);
                        if (eventinfo.EventLink != null)
                        {
                            post += (eventinfo.EventLink != null ? " Join Here!" : "");
                            int promptStart = post.IndexOf("Join Here!", StringComparison.InvariantCulture);
                            int promptEnd = promptStart + Encoding.Default.GetBytes("Join Here!").Length;
                            var index = new FacetIndex(promptStart, promptEnd);
                            var link = FacetFeature.CreateLink(eventinfo.EventLink!);
                            var facet = new Facet(index, link);
                            postCreate = atProtocol!.Repo.CreatePostAsync(post, [facet], embed);
                        }
                        else { postCreate = atProtocol!.Repo.CreatePostAsync(post, embed: embed); }

                        break;
                    case "U":
                        post = $"Update: {message}";
                        postCreate = atProtocol!.Repo.CreatePostAsync(post);
                        break;
                    default:
                        Console.WriteLine("Invalid operation");
                        return;
                }

                postCreate!.Wait();
                var postResult = postCreate.Result;
                postResult.Switch(
                    success =>
                    {
                        // Contains the ATUri and CID.
                        Console.WriteLine($"Post: {success.Uri} {success.Cid}");
                        ss.WriteString("SUCCESS");
                    },
                    error =>
                    {
                        Console.WriteLine($"Error: {error.StatusCode} {error.Detail}");
                        ss.WriteString("FAILURE");
                    });
            }
            // Catch any exception thrown just in case sumn happens
            catch (Exception e)
            {
                Console.WriteLine($"ERROR: {e.Message}");
            }
            pipeServer.Close();
        }
    }

    class EventPingInfo(string name, string desc, string? link)
    {
        public string EventName = name;
        public string EventDescription = desc;
        public string? EventLink = link;
    }

    public class StreamString
    {
        private Stream ioStream;
        private UnicodeEncoding streamEncoding;

        public StreamString(Stream ioStream)
        {
            this.ioStream = ioStream;
            streamEncoding = new UnicodeEncoding();
        }

        public string ReadString()
        {
            int len = 0;

            len = ioStream.ReadByte() * 256;
            len += ioStream.ReadByte();
            byte[] inBuffer = new byte[len];
            ioStream.Read(inBuffer, 0, len);

            return streamEncoding.GetString(inBuffer);
        }

        public int WriteString(string outString)
        {
            byte[] outBuffer = streamEncoding.GetBytes(outString);
            int len = outBuffer.Length;
            if (len > UInt16.MaxValue)
            {
                len = (int)UInt16.MaxValue;
            }
            ioStream.WriteByte((byte)(len / 256));
            ioStream.WriteByte((byte)(len & 255));
            ioStream.Write(outBuffer, 0, len);
            ioStream.Flush();

            return outBuffer.Length + 2;
        }
    }
}
