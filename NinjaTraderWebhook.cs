// ═══════════════════════════════════════════════════════════════
// BelkhayateWebhook.cs — NinjaTrader 8
//
// Détection Order Flow delta exhaustion — version calibration
//
// INSTALLATION :
// 1. NinjaTrader → Tools → Edit NinjaScript → Strategy
// 2. Nouveau → colle ce code → Compile (F5)
// 3. Ajoute sur le chart BTC APR26
// 4. Ouvre Output window (Control+F8) pour voir les deltas en live
//
// CALIBRATION :
// Regarde les lignes [DELTA] dans l'Output window.
// Quand Belkhayate montre une flèche, note le prevDelta affiché.
// Règle DeltaThreshold = cette valeur dans les propriétés.
// ═══════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.Data;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class BelkhayateWebhook : Strategy
    {
        // ── Config ────────────────────────────────────────────────
        private const string WEBHOOK_URL   = "https://web-production-365ec.up.railway.app/webhook";
        private const string WEBHOOK_TOKEN = "ninjatrader_secret_2026";
        private const string ALPACA_SYMBOL = "BTCUSD";

        [NinjaScriptProperty]
        public double DeltaThreshold { get; set; } = 1.0;   // Seuil delta absolu (baisse si pas de signaux)

        [NinjaScriptProperty]
        public int CooldownBars { get; set; } = 3;           // Bougies min entre deux signaux

        [NinjaScriptProperty]
        public bool LogDelta { get; set; } = true;           // Affiche delta dans Output window
        // ─────────────────────────────────────────────────────────

        private static readonly HttpClient http = new HttpClient();

        private double barDelta      = 0;
        private double prevDelta     = 0;
        private int    lastSignalBar = -99;
        private string lastSide      = "";
        private int    signalsSent   = 0;
        private int    signalsFiltered = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description    = "Détection Order Flow delta exhaustion → webhook Alpaca";
                Name           = "BelkhayateWebhook";
                Calculate      = Calculate.OnEachTick;
                IsOverlay      = true;
                DeltaThreshold = 1.0;
                CooldownBars   = 3;
                LogDelta       = true;
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Last)
            {
                if (e.Price >= e.Ask)
                    barDelta += e.Volume;   // acheteur agressif
                else if (e.Price <= e.Bid)
                    barDelta -= e.Volume;   // vendeur agressif
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;
            if (CurrentBar < 3) return;

            // Sauvegarde delta à la clôture de la bougie
            if (IsFirstTickOfBar)
            {
                if (LogDelta)
                    Print($"[DELTA] Bar {CurrentBar - 1} | prevDelta={prevDelta:F2} → barDelta={barDelta:F2} | Close={Close[1]:F2}");

                prevDelta = barDelta;
                barDelta  = 0;
            }

            // Cooldown
            if (CurrentBar - lastSignalBar < CooldownBars)
            {
                signalsFiltered++;
                return;
            }

            // ── Détection delta exhaustion ────────────────────────
            // BUY  : accumulation vendeuse → retournement acheteur
            bool buySignal  = prevDelta <= -DeltaThreshold && barDelta > 0;

            // SELL : accumulation acheteuse → retournement vendeur
            bool sellSignal = prevDelta >= DeltaThreshold  && barDelta < 0;

            if (LogDelta && (buySignal || sellSignal))
                Print($"[SIGNAL] {'▲ BUY' + (buySignal ? " détecté" : "") + (sellSignal ? " ▼ SELL détecté" : "")} | prevDelta={prevDelta:F2} barDelta={barDelta:F2} @ {Close[0]:F2}");

            if (buySignal && lastSide != "buy")
            {
                lastSide      = "buy";
                lastSignalBar = CurrentBar;
                signalsSent++;
                Print($"[Belkhayate] ▲ BUY #{signalsSent} | prevDelta={prevDelta:F2} barDelta={barDelta:F2} @ {Close[0]:F2}");
                SendWebhook("buy");
            }
            else if (sellSignal && lastSide != "sell")
            {
                lastSide      = "sell";
                lastSignalBar = CurrentBar;
                signalsSent++;
                Print($"[Belkhayate] ▼ SELL #{signalsSent} | prevDelta={prevDelta:F2} barDelta={barDelta:F2} @ {Close[0]:F2}");
                SendWebhook("sell");
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
                    Print($"[Webhook] ERREUR : {ex.Message}");
                }
            });
        }
    }
}
