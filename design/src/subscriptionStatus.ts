export function localSubscriptionTime(value: unknown): string | null {
  if (typeof value !== "string" || !value.trim()) return null;
  // Legacy fields named *Utc could be serialized without a zone.
  const timestamp = /(?:Z|[+-]\d{2}:\d{2})$/i.test(value) ? value : `${value}Z`;
  const date = new Date(timestamp);
  return Number.isNaN(date.getTime()) ? null : date.toLocaleString("ru-RU", {
    day: "numeric", month: "short", hour: "2-digit", minute: "2-digit"
  });
}

export function subscriptionStatus(subscription: Record<string, any>, globallyEnabled: boolean) {
  const last = localSubscriptionTime(subscription.lastUpdatedUtc);
  const next = localSubscriptionTime(subscription.nextRefreshUtc);
  return {
    last: last ? `Последнее успешное обновление: ${last}` : "Успешных обновлений ещё не было",
    next: !globallyEnabled || subscription.autoRefreshEnabled === false ? "Автообновление отключено"
      : next ? `Следующее обновление: ${next}` : "Следующее обновление: ожидает расписания",
    error: subscription.lastError ? `Ошибка последней попытки: ${String(subscription.lastError)}` : null
  };
}
