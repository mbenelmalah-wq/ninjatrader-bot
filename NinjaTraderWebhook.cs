// ═══════════════════════════════════════════════════════════════
// BelkhayateWebhook.cs — NinjaTrader 8
//
// Détection autonome de signaux Order Flow (delta exhaustion)
// Réplique la logique de Belkhayate sans dépendre du DLL
//
// INSTALLATION :
// 1. NinjaTrader → Tools → Edit NinjaScript → Strategy
// 2. Nouveau → colle ce code → Compile (F5)
// 3. Ajoute sur le chart BTC APR26
// 4. Calculate = OnEachTick pour meilleure précision
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

        // Seuils de détection — ajuste selon ton instrument
        [NinjaScriptProperty]
        public double DeltaThreshold { get; set; } = 10.0;  // delta min pour valider signal

        [NinjaScriptProperty]
        public int CooldownBars { get; set; } = 3;  // bougies minimum entre deux signaux
        // ─────────────────────────────────────────────────────────

        private static readonly HttpClient http = new HttpClient();

        // Delta calculé tick par tick
        private double barDelta     = 0;
        private double prevDelta    = 0;
        private int    lastSignalBar = -99;
        private string lastSide      = "";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description    = "Détection Order Flow delta exhaustion → webhook Alpaca";
                Name           = "BelkhayateWebhook";
                Calculate      = Calculate.OnEachTick;
                IsOverlay      = true;
                DeltaThreshold = 10.0;
                CooldownBars   = 3;
            }
            if (State == State.Configure)
            {
                // Activer les données tick pour calcul delta
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // Calcul delta : achats au Ask = positif, ventes au Bid = négatif
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
            // Traiter uniquement la série principale (pas les ticks)
            if (BarsInProgress != 0) return;
            if (CurrentBar < 3) return;

            // Cooldown : pas de signal si bougies insuffisantes
            if (CurrentBar - lastSignalBar < CooldownBars) return;

            bool buySignal  = false;
            bool sellSignal = false;

            // ── Logique delta exhaustion (Belkhayate style) ──────
            // BUY  : delta précédent très négatif → retournement positif
            if (prevDelta <= -DeltaThreshold && barDelta > 0 && Close[0] > Open[0])
                buySignal = true;

            // SELL : delta précédent très positif → retournement négatif
            if (prevDelta >= DeltaThreshold && barDelta < 0 && Close[0] < Open[0])
                sellSignal = true;

            // ── Envoi signal ──────────────────────────────────────
            if (buySignal && lastSide != "buy")
            {
                lastSide      = "buy";
                lastSignalBar = CurrentBar;
                Print($"[Belkhayate] ▲ BUY | delta={barDelta:F1} prevDelta={prevDelta:F1} @ {Close[0]}");
                SendWebhook("buy");
            }
            else if (sellSignal && lastSide != "sell")
            {
                lastSide      = "sell";
                lastSignalBar = CurrentBar;
                Print($"[Belkhayate] ▼ SELL | delta={barDelta:F1} prevDelta={prevDelta:F1} @ {Close[0]}");
                SendWebhook("sell");
            }

            // Sauvegarde delta de la bougie fermée
            if (IsFirstTickOfBar)
            {
                prevDelta = barDelta;
                barDelta  = 0;
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
