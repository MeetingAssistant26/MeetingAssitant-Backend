using System;
using System.Security.Cryptography;
using System.Text;
using Livekit.Server.Sdk.Dotnet;

var key = "test-livekit-key";
var secret = "0123456789abcdef0123456789abcdef";
var payload = "{\"x\":1}";
var receiver = new WebhookReceiver(key, secret);

string[] candidates =
{
    payload,
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
    Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
};

foreach (var c in candidates)
{
    var token = new AccessToken(key, secret).WithSha256(c).ToJwt();
    var label = c == payload ? "raw" : (c.Contains("=") ? "base64" : "hex");

    bool okDirect;
    try
    {
        receiver.Receive(payload, token);
        okDirect = true;
    }
    catch
    {
        okDirect = false;
    }

    bool okBearer;
    try
    {
        receiver.Receive(payload, $"Bearer {token}");
        okBearer = true;
    }
    catch
    {
        okBearer = false;
    }

    Console.WriteLine($"{label}: direct={okDirect}, bearer={okBearer}");
}
