# RÈGLES ABSOLUES — BOT NINJATRADER (Capital 3)

## ⛔ RÈGLES INTANGIBLES

1. **WEBHOOK ONLY** — Signaux uniquement depuis NinjaTrader via HTTP POST.
2. **PROJET SÉPARÉ** — Ne jamais toucher SPLV3 ni LuxAlgo.
3. **SELL = fermeture uniquement** — Pas de short crypto.

## ✅ ARCHITECTURE

- **Signaux** : NinjaTrader NinjaScript → POST /webhook
- **Exécution** : Alpaca Paper Trading (3ème compte)
- **Token** : ninjatrader_secret_2026
- **Trailing SL** : monitor_loop actif (30s)
- **Filtre** : Session asiatique bloquée (23:00-08:00 UTC)

## 🔑 COMPTES & URLS

| Bot | Railway URL | Token | Alpaca |
|-----|-------------|-------|--------|
| SPLV3 | `alpaca-trading-bot-production-f693.up.railway.app/webhook` | `splv3_secret_2026` | Compte 1 |
| LuxAlgo | `web-production-b2c39d.up.railway.app/webhook` | `luxalgo_secret_2026` | Compte 2 |
| NinjaTrader | `À définir après déploiement Railway` | `ninjatrader_secret_2026` | Compte 3 |
