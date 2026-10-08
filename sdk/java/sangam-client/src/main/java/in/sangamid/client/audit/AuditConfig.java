package in.sangamid.client.audit;

/**
 * The application's side of every audit event ({@code source}).
 *
 * @param appId your Sangam client id
 * @param appVersion your version
 * @param environment production, staging or development
 */
public record AuditConfig(String appId, String appVersion, String environment) {
}
