using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// The one place in HomeViz that touches the network.
//
// The promise at the head of CLAUDE.md is narrowed here, not abandoned: there is still no server to
// run, no account, no Python and no sync. What there is, is one request, sent only when a key is
// present and a button is pressed, carrying one image and returning one JSON document. Everything
// else about a home stays on the machine.
//
// It is a plain class, and the coroutine is hosted by HomeEditController, which is already a
// MonoBehaviour, so the whole feature needs NO SCENE EDIT, the property OccupancyClock and
// SelectionOverlay are both built around. There is no EditorCoroutine package here and none is
// wanted; this runs in the player as much as in the Editor.
//
// FIVE THINGS IN HERE ARE LOAD-BEARING:
//
//   * The wire format lives in BuildBody and ReadReply and nowhere else. Everything around them,
//     headers, retry, cancellation, the sentences shown to the user. Is independent of it, so
//     changing the request shape is a two-method edit rather than a hunt.
//   * stop_reason is read BEFORE content. A response can arrive with HTTP 200, an empty content
//     array and stop_reason "refusal", and code that indexes content[0] unconditionally breaks on
//     it. "The plan was cut off" and "the reply was not a plan" send a user to different places, so
//     max_tokens is distinguished too.
//   * Only 429 and 5xx are retried. A bad key retried three times is three refusals and a lockout
//     risk, and no amount of trying again fixes a 400.
//   * A call outlives the tool that started it. Abort exists because leaving the Import tab is the
//     ordinary way somebody abandons a request, and an orphaned UnityWebRequest holding a 300-second
//     timeout is a leak with a delayed callback attached.
//   * The cache breakpoint sits on the IMAGE, not on the text after it. See BuildBody. It is what
//     makes reading a plan in two passes, each with a repair turn, cost about what one pass used to,
//     and putting it one block later would silently turn every read into a write.
public sealed class ClaudeClient
{
    /// <summary>
    /// Sonnet rather than Opus, and the request body below is unchanged by that: same effort ladder,
    /// same structured output, thinking on by default when the parameter is omitted, and the same
    /// high-resolution vision tier, which is the one that matters here, because SketchImageResample
    /// caps the long edge at 2576 px and a model on the older tier would silently downscale a scan
    /// whose walls are one dark pixel wide.
    /// </summary>
    public const string Model = "claude-sonnet-5";

    /// <summary>
    /// Generous, and deliberately so. It caps thinking AND the reply together on this model, and a
    /// truncated JSON is the single likeliest way this fails. A twelve-room plan runs several
    /// thousand tokens on its own.
    /// </summary>
    public const int MaxTokens = 16000;

    private const string ENDPOINT = "https://api.anthropic.com/v1/messages";
    private const string VERSION = "2023-06-01";
    private const int TIMEOUT_SECONDS = 300;
    private const int MAX_ATTEMPTS = 3;

    public sealed class Call
    {
        public bool Done;
        public bool Aborted;

        /// <summary>The assistant's text, when it came back.</summary>
        public string Text;

        /// <summary>A sentence written to be shown verbatim, the OpeningFit convention.</summary>
        public string Error;

        public string StopReason;
        public float Elapsed;

        /// <summary>
        /// What the prefix cache did. Reported because a breakpoint that has stopped working fails
        /// SILENTLY (the answers stay correct and only the bill moves) so the one way to know it is
        /// still doing its job is to look. A run whose later turns read zero has an invalidator in it.
        /// </summary>
        public int CacheReadTokens, CacheWriteTokens;

        internal UnityWebRequest Live;

        public void Abort()
        {
            Aborted = true;
            try { Live?.Abort(); } catch { /* already finished */ }
        }
    }

    /// <summary>One turn of a conversation on the way out.</summary>
    public struct Turn
    {
        public string role;      // "user" or "assistant"
        public string text;
    }

    /// <summary>
    /// Sends the sketch and the instructions, and any prior turns for a repair round.
    /// </summary>
    public static IEnumerator Send(Call call, string system, string imageMediaType, string imageBase64,
                                   string userText, IReadOnlyList<Turn> priorTurns,
                                   string jsonSchema, string apiKey)
    {
        float began = Time.realtimeSinceStartup;
        string body = BuildBody(system, imageMediaType, imageBase64, userText, priorTurns, jsonSchema);
        byte[] payload = Encoding.UTF8.GetBytes(body);

        for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
        {
            using (var req = new UnityWebRequest(ENDPOINT, UnityWebRequest.kHttpVerbPOST))
            {
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("content-type", "application/json");
                req.SetRequestHeader("x-api-key", apiKey);
                req.SetRequestHeader("anthropic-version", VERSION);
                req.timeout = TIMEOUT_SECONDS;

                call.Live = req;
                yield return req.SendWebRequest();
                call.Live = null;
                call.Elapsed = Time.realtimeSinceStartup - began;

                if (call.Aborted) { call.Error = null; call.Done = true; yield break; }

                long status = req.responseCode;
                string text = req.downloadHandler != null ? req.downloadHandler.text : null;

                if (req.result == UnityWebRequest.Result.Success && status >= 200 && status < 300)
                {
                    call.Error = ReadReply(call, text);
                    call.Done = true;
                    yield break;
                }

                bool retryable = status == 429 || status >= 500
                              || req.result == UnityWebRequest.Result.ConnectionError;

                if (!retryable || attempt == MAX_ATTEMPTS)
                {
                    call.Error = Explain(req, status, text, attempt);
                    call.Done = true;
                    yield break;
                }

                float wait = Backoff(req, attempt);
                float until = Time.realtimeSinceStartup + wait;
                while (Time.realtimeSinceStartup < until)
                {
                    if (call.Aborted) { call.Done = true; yield break; }
                    yield return null;
                }
            }
        }
    }

    // ===========================================================================================
    // ANTHROPIC MESSAGES API: the wire format lives here and nowhere else
    // ===========================================================================================

    private static string BuildBody(string system, string mediaType, string imageBase64,
                                    string userText, IReadOnlyList<Turn> priorTurns, string jsonSchema)
    {
        var root = new JObject
        {
            ["model"] = Model,
            ["max_tokens"] = MaxTokens,
            ["system"] = system,
        };

        // Structured output. This is what makes the reply valid against the schema by construction
        // rather than by parsing hope, and it is why nothing here has to strip markdown fences or
        // hunt for the first brace.
        root["output_config"] = new JObject
        {
            ["effort"] = "high",
            ["format"] = new JObject
            {
                ["type"] = "json_schema",
                ["schema"] = JToken.Parse(jsonSchema),
            },
        };

        // Deliberately absent: temperature, top_p, top_k (all rejected on this model), any thinking
        // configuration (it is on by default here, and reading a floor plan is exactly the spatial
        // reasoning it is for), and an assistant prefill (rejected too, and made pointless by the
        // structured output above).

        var messages = new JArray();

        // THE CACHE BREAKPOINT GOES ON THE IMAGE, AND WHERE IT SITS IS THE WHOLE OF IT.
        //
        // Caching is a prefix match: everything before the breakpoint is reused, everything after it
        // is charged in full. What is genuinely stable across every request of a run is the system
        // prompt and the sketch: the instructions and the catalog do not change, and the image is the
        // same image whether we are asking for rooms, asking for the fittings, or asking for a repair.
        // What varies is the sentence after it. So the breakpoint belongs on the last block of that
        // stable half, which is the image, and NOT on the trailing text: a breakpoint after the text
        // would key the entry to one particular question and every later turn would write a fresh
        // entry instead of reading this one.
        //
        // That is what pays for the rest of this feature. A sketch runs to a few thousand image tokens
        // on top of a system prompt that carries all 35 catalog items, and the run asks four or five
        // questions of it: two passes, a repair turn on each, a second room candidate. Reading that
        // prefix at a tenth of the price is what makes reading the plan twice cost about what reading
        // it once used to.
        //
        // Five minutes is the default lifetime and it is the right one here: the turns are seconds
        // apart, and the one-hour tier costs twice as much to write for a run that is over long before
        // it expires.
        var image = new JObject
        {
            ["type"] = "image",
            ["source"] = new JObject
            {
                ["type"] = "base64",
                ["media_type"] = mediaType,
                ["data"] = imageBase64,
            },
            ["cache_control"] = new JObject { ["type"] = "ephemeral" },
        };

        var first = new JArray
        {
            image,
            new JObject { ["type"] = "text", ["text"] = userText },
        };
        messages.Add(new JObject { ["role"] = "user", ["content"] = first });

        if (priorTurns != null)
            foreach (var t in priorTurns)
                messages.Add(new JObject { ["role"] = t.role, ["content"] = t.text });

        root["messages"] = messages;
        return root.ToString(Newtonsoft.Json.Formatting.None);
    }

    /// <summary>Returns an error sentence, or null after filling the call in.</summary>
    private static string ReadReply(Call call, string json)
    {
        JObject o;
        try { o = JObject.Parse(json); }
        catch { return "The reply could not be read."; }

        var usage = o["usage"];
        if (usage != null)
        {
            call.CacheReadTokens = (int?)usage["cache_read_input_tokens"] ?? 0;
            call.CacheWriteTokens = (int?)usage["cache_creation_input_tokens"] ?? 0;
        }

        string stopReason = (string)o["stop_reason"];
        call.StopReason = stopReason;

        // Checked before content is touched: a refusal arrives as a perfectly successful response
        // with nothing in it.
        if (stopReason == "refusal")
            return "Claude declined to read this image. If it is an ordinary floor plan, this is "
                 + "worth reporting.";

        var content = o["content"] as JArray;
        if (content != null)
        {
            var sb = new StringBuilder();
            foreach (var block in content)
                if ((string)block["type"] == "text") sb.Append((string)block["text"]);
            call.Text = sb.ToString();
        }

        if (stopReason == "max_tokens")
            return "The plan was cut off before it finished. This sketch may have too many rooms to "
                 + "read in one pass.";

        if (string.IsNullOrWhiteSpace(call.Text))
            return "The reply came back empty.";

        return null;
    }

    // ===========================================================================================

    private static string Explain(UnityWebRequest req, long status, string body, int attempts)
    {
        if (req.result == UnityWebRequest.Result.ConnectionError)
            return "Could not reach the API. Check the network connection.";

        switch (status)
        {
            case 400: return "The request was rejected as malformed. " + Detail(body);
            case 401: return "That API key was rejected. Check it in the Sketch panel.";
            case 403: return "That API key is not allowed to use this model.";
            case 404: return "The API endpoint was not found.";
            case 413: return "The sketch was too large to send.";
            case 429: return $"Rate limited after {attempts} attempts. Wait a minute and try again.";
            case 529: return "Claude is overloaded. Try again shortly.";
        }

        if (status >= 500) return $"The API returned an error ({status}). Try again shortly.";
        return $"The request failed ({status}). " + Detail(body);
    }

    /// <summary>The API's own message when it has one. It is usually the specific thing that is wrong.</summary>
    private static string Detail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            var message = (string)JObject.Parse(body)["error"]?["message"];
            return string.IsNullOrWhiteSpace(message) ? "" : message;
        }
        catch { return ""; }
    }

    /// <summary>Honours retry-after when the API sends one; otherwise backs off with a little jitter.</summary>
    private static float Backoff(UnityWebRequest req, int attempt)
    {
        string header = req.GetResponseHeader("retry-after");
        if (!string.IsNullOrEmpty(header) && float.TryParse(header, out float seconds))
            return Mathf.Clamp(seconds, 1f, 60f);

        return Mathf.Pow(3f, attempt) + Random.Range(0f, 1.5f);
    }
}
