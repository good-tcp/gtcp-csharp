using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Threading;
using System.IO;
using misc;

namespace gtcp
{
    delegate void callback(params object[] args);

    public class Client
    {
        private static Stream stm;
        private static RSACryptoServiceProvider clientRsa;
        private static RSACryptoServiceProvider serverRsa;
        private static Dictionary<object, Delegate> event2callback = new Dictionary<object, Delegate>();
        private static Dictionary<string, Delegate> callbacks = new Dictionary<string, Delegate>();
        public string id;

        private void handleBytes(byte[] bytes)
        {
            //if (clientRsa != default(RSACryptoServiceProvider)) bytes = clientRsa.Decrypt(bytes, true);
            int[] lengths = Struct.unpack("ii", bytes.Take(8).ToArray()).Cast<int>().ToArray();

            //object[] data = Struct.unpack(Encoding.UTF8.GetString((byte[])Struct.unpack(string.Format("{0}s", lengths[0]), bytes.Skip(8).Take(lengths[0]).ToArray())[0]), bytes.Skip(8 + lengths[0]).Take(lengths[1]).ToArray());
            object[] data = Struct.unpack($"{lengths[0]}s{lengths[1]}s", bytes.Skip(8).Take(lengths.Sum()).ToArray());
            if (clientRsa != default(RSACryptoServiceProvider)) data[1] = clientRsa.Decrypt((byte[])data[1], true);
            data = Struct.unpack(Encoding.UTF8.GetString((byte[])data[0]), (byte[])data[1]);
            data = data.Select((object item, int i) => item.GetType().Name == "Byte[]" ? Encoding.UTF8.GetString((byte[])item) : item).ToArray();

            if (data[0].GetType().Name == "String")
            {
                string fid = (string)data[0];

                if (new string(fid.Take(15).ToArray()) == "@gtcp:callback:")
                {
                    callbacks[new string(fid.Skip(15).ToArray())].DynamicInvoke(data.Skip(1).ToArray());
                    callbacks.Remove(new string(fid.Skip(15).ToArray()));
                    return;
                }
            }

            if (event2callback.ContainsKey(data[0]))
            {
                for (var i = 0; i < data.Length; i++)
                {
                    if (data[i].GetType().Name == "String")
                    {
                        if (new string(data[i].ToString().Take(15).ToArray()) == "@gtcp:callback:")
                        {
                            object[] datasi = new object[data.Length];
                            Array.Copy(data, datasi, data.Length);
                            int index = i;

                            void callbackm(params object[] args)
                            {
                                emit(new object[1] { id + ":" + (string)datasi[index] }.Concat(args).ToArray());
                            }

                            callback cbem = callbackm;

                            data[i] = cbem;
                        }
                    }
                }
                event2callback[data[0]].DynamicInvoke(data.Skip(1).ToArray());
            }
            if (lengths.Sum() < bytes.Length - 8) handleBytes(bytes.Skip(8 + lengths.Sum()).ToArray());
        }

        public Client(string ips, params object[] extras)
        {
            Delegate cb = (Delegate)extras.Where((i) => i.GetType().Name == "Action`1").FirstOrDefault();
            Dictionary<object, object> options = (Dictionary<object, object>)extras.Where((i) => i.GetType().Name == "Dictionary`2").FirstOrDefault();

            TcpClient c = new TcpClient();
            string[] ip = ips.Split(':');
            c.Connect(ip[0], int.Parse(ip[1]));
            stm = c.GetStream();

            if (options != default(Dictionary<object, object>))
            {
                if (options.ContainsKey("encrypted")) if ((bool)options["encrypted"]) clientRsa = new RSACryptoServiceProvider(2048);
            }

            byte[] message = Encoding.UTF8.GetBytes(clientRsa == default(RSACryptoServiceProvider) ? new char[1] : RSAtools.ExportPublicKeyToPEMFormat(clientRsa).ToCharArray());
            stm.Write(message, 0, message.Length);

            byte[] keyBytes = new byte[1024];
            int keyBytesRead = stm.Read(keyBytes, 0, 1024);
            keyBytes = keyBytes.Take(keyBytesRead).ToArray();

            id = Encoding.UTF8.GetString(keyBytes.Take(36).ToArray());

            if (keyBytesRead != 36)
            {
                if (Encoding.UTF8.GetString(keyBytes.Skip(36).Take(26).ToArray()) == "-----BEGIN PUBLIC KEY-----")
                {
                    serverRsa = new RSACryptoServiceProvider();
                    //serverRsa.ImportFromPem(Encoding.UTF8.GetChars(keyBytes.Skip(36).Take(450).ToArray()));
                    keyBytes = keyBytes.Take(36).Concat(keyBytes.Skip(486)).ToArray();
                }
            }

            if (cb != default(Delegate)) cb.DynamicInvoke(this);

            if (keyBytes.Length != 36)
            {
                handleBytes(keyBytes.Skip(36).ToArray());
            }

            Thread t = new Thread(() =>
            {
                while (true)
                {
                    byte[] bytes = new byte[1024];
                    int bytesread = stm.Read(bytes, 0, 1024);

                    bytes = bytes.Take(bytesread).ToArray();
                    handleBytes(bytes);
                }
            });
            t.Start();
        }
        public void on(object e, Delegate cb)
        {
            event2callback.Add(e, cb);
        }
        public void emit(params object[] data)
        {
            string types = "";

            for (int i = 0; i < data.Length; i++)
            {
                string dtype = data[i].GetType().Name;

                if (!new string[3] { "String", "Single", "Int32" }.Contains(dtype) && !dtype.Contains("Action"))
                {
                    throw new Exception("Data includes unsupported type");
                }

                if (dtype == "String")
                {
                    byte[] inbytes = Encoding.UTF8.GetBytes((string)data[i]);
                    data[i] = inbytes;
                    types += string.Format("{0}s", ((byte[])data[i]).Length);
                }
                else if (dtype.Contains("Action"))
                {
                    string cbid = Guid.NewGuid().ToString();
                    callbacks[cbid] = (Delegate)data[i];
                    types += "51s";
                    data[i] = Encoding.UTF8.GetBytes("@gtcp:callback:" + cbid);
                }
                else
                {
                    types += dtype == "Int32" ? "i" : "f";
                }
            }

            byte[] packeddata = Struct.pack(types, data);
            if (serverRsa != default(RSACryptoServiceProvider)) packeddata = serverRsa.Encrypt(packeddata, true);
            packeddata = Struct.pack($"ii{types.Length}s{packeddata.Length}s", new object[4] { types.Length, packeddata.Length, Encoding.UTF8.GetBytes(types), packeddata });
            stm.Write(packeddata, 0, packeddata.Length);
        }
    }
}
