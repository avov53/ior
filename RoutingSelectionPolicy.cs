namespace HappBox;
public static class RoutingSelectionPolicy {
 public static HappRoutingProfile? Effective(AppState state) => !state.RoutingEnabled || string.IsNullOrEmpty(state.SelectedHappRoutingProfileId) ? Empty() : state.HappRoutingProfiles.FirstOrDefault(r=>r.Id==state.SelectedHappRoutingProfileId);
 private static HappRoutingProfile Empty()=>new(){Source=RoutingProfileSource.Ior,GlobalProxy=true};
 public static void ForServer(AppState state,ProxyProfile? server){var id=state.Subscriptions.FirstOrDefault(s=>s.Id==server?.SubscriptionId)?.RoutingProfileId;state.SelectedHappRoutingProfileId=state.HappRoutingProfiles.Any(r=>r.Id==id)?id:null;}
 public static void Migrate(AppState state){if(!state.RoutingToggleMigrated){state.RoutingEnabled=state.SelectedHappRoutingProfileId!="none";state.RoutingToggleMigrated=true;}state.HappRoutingProfiles.RemoveAll(r=>r.Id=="none");if(state.SelectedHappRoutingProfileId=="none")state.SelectedHappRoutingProfileId=null;foreach(var sub in state.Subscriptions){sub.RouteConsent="accepted";if(sub.PendingRoute is {} pending){try{SubscriptionService.RestoreRouting(state,sub,pending);}catch(Exception error) when(error is FormatException or InvalidOperationException or System.Text.Json.JsonException){sub.ImportWarning="Не удалось восстановить маршрут подписки. Обновите подписку.";}}}}
}
