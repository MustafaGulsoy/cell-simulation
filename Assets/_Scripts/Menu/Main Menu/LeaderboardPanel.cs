using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// "Top players" overlay for the main menu: asks the server for the persistent leaderboard over UDP (the
// HTTP port isn't public) and lists the best runs for today / this week / all time. The request is made
// on a background thread with a short timeout and one retry, so an offline server just shows a message.
public class LeaderboardPanel : MonoBehaviour
{
    private const byte MsgTop = 7;
    private const byte MsgTopList = 8;

    private GameObject panel;
    private TextMeshProUGUI body;
    private Canvas canvas;
    private readonly Queue<Action> mainThread = new Queue<Action>();
    private readonly object gate = new object();
    private int requestSerial;

    public static LeaderboardPanel Create(Transform openButtonParent, Vector2 anchor, Vector2 pivot, Vector2 offset)
    {
        var go = new GameObject("LeaderboardPanel");
        var lp = go.AddComponent<LeaderboardPanel>();
        lp.Build(openButtonParent, anchor, pivot, offset);
        return lp;
    }

    private void Build(Transform buttonParent, Vector2 anchor, Vector2 pivot, Vector2 offset)
    {
        canvas = RuntimeUi.CreateCanvas("LeaderboardCanvas", 45);
        canvas.transform.SetParent(transform, false);

        // Full-screen dim + centred card, hidden until opened.
        var dim = RuntimeUi.Panel("Panel", canvas.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000f, 4000f), new Color(0f, 0f, 0f, 0.6f));
        dim.raycastTarget = true;
        panel = dim.gameObject;

        var card = RuntimeUi.Panel("Card", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(900f, 1150f), new Color(0.07f, 0.09f, 0.15f, 0.97f));
        RuntimeUi.Label("Title", card.transform, "Top players", 56f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -26f), new Vector2(860f, 80f), TextAlignmentOptions.Center);

        string[] tabs = { "Today", "This week", "All time" };
        for (int i = 0; i < 3; i++)
        {
            byte period = (byte)i;
            var tab = RuntimeUi.ButtonWithLabel("Tab" + i, card.transform, tabs[i], 36f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2((i - 1) * 270f, -120f), new Vector2(250f, 74f), new Color(0.2f, 0.28f, 0.45f, 1f));
            tab.onClick.AddListener(delegate { Request(period); });
        }

        body = RuntimeUi.Label("Body", card.transform, "", 40f, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -220f), new Vector2(820f, 780f), TextAlignmentOptions.TopLeft);
        body.enableWordWrapping = false;

        var close = RuntimeUi.ButtonWithLabel("Close", card.transform, "Close", 40f, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(300f, 84f), new Color(0.5f, 0.2f, 0.2f, 1f));
        close.onClick.AddListener(delegate { panel.SetActive(false); GameAudio.Play("click"); });

        panel.SetActive(false);

        // The button that opens it, placed by the caller in whatever menu layout exists.
        var open = RuntimeUi.ButtonWithLabel("OpenLeaderboard", buttonParent, "Top players", 38f, anchor, pivot, offset, new Vector2(330f, 84f), new Color(0.15f, 0.2f, 0.34f, 0.92f));
        open.onClick.AddListener(delegate
        {
            panel.SetActive(true);
            GameAudio.Play("click");
            Request(2);
        });
    }

    private void Update()
    {
        lock (gate)
        {
            while (mainThread.Count > 0) mainThread.Dequeue().Invoke();
        }
    }

    private void Request(byte period)
    {
        int serial = ++requestSerial;
        body.text = "Loading...";
        string host = ServerAddress.Host(MainMenuHandler.DEFAULT_SERVER_IP);
        int port = ServerAddress.Port(7778);

        var thread = new Thread(delegate ()
        {
            string result;
            try
            {
                result = Fetch(host, port, period);
            }
            catch (Exception)
            {
                result = null;
            }
            lock (gate)
            {
                mainThread.Enqueue(delegate
                {
                    if (serial != requestSerial) return; // a newer tab was tapped meanwhile
                    body.text = result ?? "Couldn't reach the server.";
                });
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private static string Fetch(string host, int port, byte period)
    {
        using (var udp = new UdpClient())
        {
            udp.Client.ReceiveTimeout = 1500;
            udp.Connect(host, port);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                udp.Send(new byte[] { MsgTop, period }, 2);
                try
                {
                    var remote = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref remote);
                    if (data.Length == 0 || data[0] != MsgTopList) continue;
                    using (var ms = new MemoryStream(data, 1, data.Length - 1))
                    using (var r = new BinaryReader(ms))
                    {
                        return Format(TopList.Decode(r));
                    }
                }
                catch (SocketException)
                {
                    // timed out: try once more
                }
            }
        }
        return null;
    }

    private static string Format(TopList list)
    {
        if (list.Names.Count == 0)
        {
            return "No runs recorded yet.\nBe the first!";
        }

        var lines = new System.Text.StringBuilder();
        for (int i = 0; i < list.Names.Count; i++)
        {
            int total = Mathf.Max(0, Mathf.RoundToInt(list.Seconds[i]));
            lines.AppendFormat("{0,2}.  {1}\n", i + 1, list.Names[i]);
            lines.AppendFormat("      <color=#9fb3d9>{0} mass  -  {1}:{2:00}</color>\n", Mathf.RoundToInt(list.Masses[i]), total / 60, total % 60);
        }
        return lines.ToString();
    }
}
