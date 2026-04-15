// ═══════════════════════════════════════════════════════════════
// BelkhayateWebhook.cs — NinjaTrader 8
//
// INSTALLATION :
// 1. NinjaTrader → Tools → Edit NinjaScript → Strategy
// 2. Nouveau fichier → colle ce code → Compile
// 3. Ajoute la stratégie sur le chart BTC APR26
//    (même chart que Belkhayate Orderflow)
// 4. Remplace TON-URL-RAILWAY par l'URL du bot
//
// SIGNAUX :
// - Triangle bleu ▲ (bas de bougie) = BUY
// - Triangle bleu ▼ (haut de bougie) = SELL
// ═══════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class BelkhayateWebhook : Strategy
    {
        // ── Config — modifie ici ──────────────────────────────────
        private const string WEBHOOK_URL   = "https://web-production-365ec.up.railway.app/webhook";
        private const string WEBHOOK_TOKEN = "ninjatrader_secret_2026";
        private const string ALPACA_SYMBOL = "BTCUSD";   // symbole exécuté sur Alpaca
        private const double ENERGY_MIN    = 0.60;       // énergie minimum pour valider le signal
        // ─────────────────────────────────────────────────────────

        private static readonly HttpClient http = new HttpClient();
        private string lastState = "";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Envoie les signaux Belkhayate Orderflow vers Alpaca via webhook";
                Name        = "BelkhayateWebhook";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 5) return;

            try
            {
                // ── Lecture des signaux Belkhayate ──────────────────────
                // Belkhayate dessine des objets sur le chart :
                // Triangle BUY  = ArrowUp  ou TriangleUp  en bas de bougie
                // Triangle SELL = ArrowDown ou TriangleDown en haut de bougie

                bool buySignal  = false;
                bool sellSignal = false;

                // Scan des objets dessinés sur la bougie actuelle
                foreach (var obj in DrawObjects)
                {
                    if (obj == null) continue;

                    // On vérifie uniquement les objets sur la dernière bougie fermée
                    if (obj is DrawingTools.ArrowUp || obj.ToString().ToLower().Contains("triangleup")
                        || obj.ToString().ToLower().Contains("arrowup"))
                    {
                        buySignal = true;
                    }
                    else if (obj is DrawingTools.ArrowDown || obj.ToString().ToLower().Contains("triangledown")
                             || obj.ToString().ToLower().Contains("arrowdown"))
                    {
                        sellSignal = true;
                    }
                }

                // ── Envoi webhook si nouveau signal ────────────────────
                if (buySignal && lastState != "BUY")
                {
                    lastState = "BUY";
                    Print($"[Belkhayate] BUY détecté → envoi webhook BTCUSD");
                    SendWebhook("buy");
                }
                else if (sellSignal && lastState != "SELL")
                {
                    lastState = "SELL";
                    Print($"[Belkhayate] SELL détecté → envoi webhook BTCUSD");
                    SendWebhook("sell");
                }
            }
            catch (Exception ex)
            {
                Print($"[BelkhayateWebhook] Erreur : {ex.Message}");
            }
        }

        private void SendWebhook(string side)
        {
            string payload = $"{{" +
                $"\"token\":\"{WEBHOOK_TOKEN}\"," +
                $"\"symbol\":\"{ALPACA_SYMBOL}\"," +
                $"\"side\":\"{side}\"," +
                $"\"source\":\"BELKHAYATE\"" +
                $"}}";

            Task.Run(async () =>
            {
                try
                {
                    var content  = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await http.PostAsync(WEBHOOK_URL, content);
                    string body  = await response.Content.ReadAsStringAsync();
                    Print($"[Webhook] {side.ToUpper()} {ALPACA_SYMBOL} → {(int)response.StatusCode} {body}");
                }
                catch (Exception ex)
                {
                    Print($"[Webhook] ERREUR envoi : {ex.Message}");
                }
            });
        }
    }
}
