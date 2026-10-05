import assert from "node:assert/strict";
import { localSubscriptionTime, subscriptionStatus } from "../src/subscriptionStatus.ts";

process.env.TZ = "Europe/Moscow";
const status = subscriptionStatus({ lastUpdatedUtc: "2026-10-04T17:42:00Z", nextRefreshUtc: "2026-10-04T18:42:00Z", lastError: "request failed" }, true);
assert.match(status.last, /Последнее успешное обновление:.*20:42/);
assert.match(status.next, /Следующее обновление:.*21:42/);
assert.equal(status.error, "Ошибка последней попытки: request failed");
assert.equal(localSubscriptionTime("2026-10-04T17:42:00"), localSubscriptionTime("2026-10-04T20:42:00+03:00"));
assert.equal(localSubscriptionTime("invalid"), null);
assert.equal(subscriptionStatus({}, true).last, "Успешных обновлений ещё не было");
assert.equal(subscriptionStatus({}, false).next, "Автообновление отключено");
process.env.TZ = "Europe/Berlin";
assert.match(localSubscriptionTime("2026-10-04T17:42:00Z"), /19:42/);
console.log("Subscription status: UTC, local zones, next vs successful update and failure labels passed.");
