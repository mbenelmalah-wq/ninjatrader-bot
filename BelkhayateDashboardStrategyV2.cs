// ═══════════════════════════════════════════════════════════════
// BelkhayateDashboardStrategyV2.cs — NinjaTrader 8
//
// V2 — Améliorations vs V1 :
//   - DeltaSlopeThreshold configurable (était 0.2 fixe)
//   - AlpacaSymbol configurable (conservé)
//   - LogSignals : affiche dans Output chaque signal détecté
//   - Compteur signaux envoyés / filtrés
//   - Webhook : réponse bot affichée dans Output
//   - Ne PAS écraser BelkhayateDashboardStrategy.cs
//
// INSTALLATION :
// NinjaTrader → Tools → Edit NinjaScript → Strategy → Nouveau
// Nom : BelkhayateDashboardStrategyV2 → colle ce code → F5
// ═══════════════════════════════════════════════════════════════

#region Using declarations
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.Data;
#endregion
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class BelkhayateDashboardStrategyV2 : Strategy
    {
        private const string WEBHOOK_URL   = "https://web-production-365ec.up.railway.app/webhook";
        private const string WEBHOOK_TOKEN = "ninjatrader_secret_2026";

        // ── Paramètres configurables ──────────────────────────────
        [NinjaScriptProperty]
        public string AlpacaSymbol { get; set; } = "BTCUSD";         // Ex: BTCUSD, ETHUSD, SOLUSD

        [NinjaScriptProperty]
        public int CooldownBars { get; set; } = 3;                    // Bougies min entre deux signaux

        [NinjaScriptProperty]
        public double DeltaSlopeThreshold { get; set; } = 0.2;        // Seuil deltaSlope (baisse si peu de signaux)

        [NinjaScriptProperty]
        public double IcebergVolMult { get; set; } = 1.5;             // Multiplicateur volume iceberg

        [NinjaScriptProperty]
        public double VacuumEnergyMin { get; set; } = 2.0;            // Énergie min signal vacuum

        [NinjaScriptProperty]
        public bool LogSignals { get; set; } = true;                  // Logs dans Output window
        // ─────────────────────────────────────────────────────────

        private static readonly HttpClient http = new HttpClient();

        private EMA emaFast;
        private EMA emaSlow;
        private ATR atr;
        private SMA volMA;

        private Series<double> delta;
        private Series<double> deltaSlope;
        private Series<double> deltaAbsSeries;

        private int    lastSignalBar    = -99;
        private string lastSide         = "";
        private int    signalsSent      = 0;
        private int    signalsFiltered  = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                 = "BelkhayateDashboardStrategyV2";
                Calculate            = Calculate.OnBarClose;
                IsOverlay            = true;
                AlpacaSymbol         = "BTCUSD";
                CooldownBars         = 3;
                DeltaSlopeThreshold  = 0.2;
                IcebergVolMult       = 1.5;
                VacuumEnergyMin      = 2.0;
                LogSignals           = true;
            }
            else if (State == State.DataLoaded)
            {
                emaFast = EMA(50);
                emaSlow = EMA(100);
                atr     = ATR(14);
                volMA   = SMA(Volume, 20);

                delta          = new Series<double>(this);
                deltaSlope     = new Series<double>(this);
                deltaAbsSeries = new Series<double>(this);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 100) return;

            // ── Indicateurs ───────────────────────────────────────
            double gravity  = (emaFast[0] + emaSlow[0]) / 2;
            double range    = High[0] - Low[0];
            double energy   = atr[0] > 0 ? range / atr[0] : 0;
            double volNorm  = Volume[0] / volMA[0];

            delta[0]           = (Close[0] - Open[0]) * Volume[0];
            double deltaFast   = EMA(delta, 5)[0];
            deltaAbsSeries[0]  = Math.Abs(delta[0]);
            double deltaAbs    = EMA(deltaAbsSeries, 20)[0];
            deltaSlope[0]      = deltaAbs > 0 ? deltaFast / deltaAbs : 0;

            // ── Conditions de marché ──────────────────────────────
            bool trendUp     = Close[0] > emaSlow[0];
            bool trendDown   = Close[0] < emaSlow[0];
            bool expansion   = energy > 1.0;
            bool compression = energy < 0.8;

            bool breakoutBuy  = Close[0] > MAX(High, 15)[1];
            bool breakoutSell = Close[0] < MIN(Low, 15)[1];

            bool iceberg     = Volume[0] > volMA[0] * IcebergVolMult && Math.Abs(Close[0] - Open[0]) < atr[0] * 0.3;
            bool icebergBuy  = iceberg && Close[0] > gravity;
            bool icebergSell = iceberg && Close[0] < gravity;

            bool vacuumBuy  = energy > VacuumEnergyMin && volNorm > 1.5 && Close[0] > gravity;
            bool vacuumSell = energy > VacuumEnergyMin && volNorm > 1.5 && Close[0] < gravity;

            // ── Signaux composites ────────────────────────────────
            bool breakoutSignalBuy  = breakoutBuy  && expansion   && deltaSlope[0] >  DeltaSlopeThreshold;
            bool breakoutSignalSell = breakoutSell && expansion   && deltaSlope[0] < -DeltaSlopeThreshold;
            bool reversalBuy        = icebergBuy   && compression && deltaSlope[0] >  0;
            bool reversalSell       = icebergSell  && compression && deltaSlope[0] <  0;
            bool vacuumSignalBuy    = vacuumBuy    && expansion;
            bool vacuumSignalSell   = vacuumSell   && expansion;
            bool momentumBuy        = deltaSlope[0] >  DeltaSlopeThreshold && Close[0] > gravity;
            bool momentumSell       = deltaSlope[0] < -DeltaSlopeThreshold && Close[0] < gravity;

            bool buySignal  = (breakoutSignalBuy  || reversalBuy  || vacuumSignalBuy  || momentumBuy)  && trendUp;
            bool sellSignal = (breakoutSignalSell || reversalSell || vacuumSignalSell || momentumSell) && trendDown;

            // ── Flèches sur le chart ──────────────────────────────
            if (buySignal)
                Draw.ArrowUp(this,   "BUY"  + CurrentBar, true, 0, Low[0]  - TickSize, Brushes.Lime);
            if (sellSignal)
                Draw.ArrowDown(this, "SELL" + CurrentBar, true, 0, High[0] + TickSize, Brushes.Red);

            // ── Log calibration ───────────────────────────────────
            if (LogSignals && (buySignal || sellSignal))
            {
                string type = breakoutSignalBuy || breakoutSignalSell ? "BREAKOUT"
                            : reversalBuy       || reversalSell       ? "REVERSAL"
                            : vacuumSignalBuy   || vacuumSignalSell   ? "VACUUM"
                            : "MOMENTUM";
                Print($"[V2] {(buySignal ? "▲ BUY" : "▼ SELL")} [{type}] | deltaSlope={deltaSlope[0]:F3} energy={energy:F2} volNorm={volNorm:F2} @ {Close[0]:F2}");
            }

            // ── Cooldown ──────────────────────────────────────────
            if (CurrentBar - lastSignalBar < CooldownBars)
            {
                if (buySignal || sellSignal)
                {
                    signalsFiltered++;
                    if (LogSignals)
                        Print($"[V2] Signal filtré cooldown ({signalsFiltered} total filtrés)");
                }
                return;
            }

            // ── Envoi webhook ─────────────────────────────────────
            if (buySignal && lastSide != "buy")
            {
                lastSide      = "buy";
                lastSignalBar = CurrentBar;
                signalsSent++;
                Print($"[V2] ▲ BUY #{signalsSent} envoyé → {AlpacaSymbol} @ {Close[0]:F2}");
                SendWebhook("buy");
            }
            else if (sellSignal && lastSide != "sell")
            {
                lastSide      = "sell";
                lastSignalBar = CurrentBar;
                signalsSent++;
                Print($"[V2] ▼ SELL #{signalsSent} envoyé → {AlpacaSymbol} @ {Close[0]:F2}");
                SendWebhook("sell");
            }
        }

        private void SendWebhook(string side)
        {
            string payload = "{\"token\":\"" + WEBHOOK_TOKEN + "\",\"symbol\":\"" + AlpacaSymbol + "\",\"side\":\"" + side + "\",\"source\":\"BELKHAYATE\"}";
            Task.Run(async () =>
            {
                try
                {
                    var content  = new StringContent(payload, Encoding.UTF8, "application/json");
                    var response = await http.PostAsync(WEBHOOK_URL, content);
                    string body  = await response.Content.ReadAsStringAsync();
                    Print("[V2][Webhook] " + side.ToUpper() + " " + AlpacaSymbol + " → " + (int)response.StatusCode + " " + body);
                }
                catch (Exception ex)
                {
                    Print("[V2][Webhook] ERREUR : " + ex.Message);
                }
            });
        }
    }
}
