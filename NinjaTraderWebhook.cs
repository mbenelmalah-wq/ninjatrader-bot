// ═══════════════════════════════════════════════════════════════
// NinjaTraderWebhook.cs
// Colle ce code dans NinjaTrader : New → NinjaScript → Strategy
// Adapte les conditions de signal à ta stratégie
// ═══════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class NinjaTraderWebhook : Strategy
    {
        // ── Paramètres (modifiables dans l'UI NinjaTrader) ────────────
        private string WebhookUrl   = "https://TON-URL-RAILWAY.up.railway.app/webhook";
        private string WebhookToken = "ninjatrader_secret_2026";

        private static readonly HttpClient http = new HttpClient();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Envoie les signaux BUY/SELL vers le bot Alpaca via webhook";
                Name        = "NinjaTraderWebhook";
                Calculate   = Calculate.OnBarClose;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 20) return;

            string symbol = Instrument.FullName.Replace(" ", "").Replace("/", "").ToUpper();

            // ════════════════════════════════════════════════════════════
            // ⬇ ADAPTE ICI TA STRATÉGIE
            // Exemple : croisement EMA 9 / 21
            // ════════════════════════════════════════════════════════════
            double ema9  = EMA(9)[0];
            double ema21 = EMA(21)[0];
            double ema9_prev  = EMA(9)[1];
            double ema21_prev = EMA(21)[1];

            bool signalBuy  = ema9_prev <= ema21_prev && ema9 > ema21;   // croisement haussier
            bool signalSell = ema9_prev >= ema21_prev && ema9 < ema21;   // croisement baissier
            // ════════════════════════════════════════════════════════════

            if (signalBuy)
            {
                SendWebhook(symbol, "buy");
            }
            else if (signalSell)
            {
                SendWebhook(symbol, "sell");
            }
        }

        private void SendWebhook(string symbol, string side)
        {
            string payload = $"{{\"token\":\"{WebhookToken}\",\"symbol\":\"{symbol}\",\"side\":\"{side}\",\"source\":\"NINJATRADER\"}}";
            Task.Run(async () =>
            {
                try
                {
                    var content  = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await http.PostAsync(WebhookUrl, content);
                    string body  = await response.Content.ReadAsStringAsync();
                    Print($"[Webhook] {side.ToUpper()} {symbol} → {response.StatusCode} {body}");
                }
                catch (Exception ex)
                {
                    Print($"[Webhook] ERREUR : {ex.Message}");
                }
            });
        }
    }
}
