package in.sangamid.spring;

import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import java.io.IOException;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.oauth2.client.oidc.web.logout.OidcClientInitiatedLogoutSuccessHandler;
import org.springframework.security.oauth2.client.registration.ClientRegistrationRepository;
import org.springframework.security.web.authentication.AuthenticationSuccessHandler;
import org.springframework.http.HttpMethod;
import org.springframework.security.web.servlet.util.matcher.PathPatternRequestMatcher;

/** Applies Sign in with Sangam to a {@link HttpSecurity}: the routes, PKCE, step-up and where people come back to. */
public final class SangamSecurity {
    private SangamSecurity() {
    }

    static boolean wantsJson(HttpServletRequest request) {
        String accept = request.getHeader("Accept");
        return accept != null && accept.contains("application/json") || "fetch".equals(request.getHeader("X-Requested-With"));
    }

    /** The address that starts a sign-in, with a step-up requirement when given. */
    public static String loginUrl(SangamProperties properties, String returnTo, String acr, Long maxAge) {
        StringBuilder url = new StringBuilder(properties.loginPath()).append("?returnTo=").append(URLEncoder.encode(returnTo, StandardCharsets.UTF_8));
        if (acr != null) {
            url.append("&acr=").append(URLEncoder.encode(acr, StandardCharsets.UTF_8));
            if (maxAge != null) {
                url.append("&max_age=").append(maxAge);
            }
        }
        return url.toString();
    }

    /**
     * Configures OIDC sign-in at {basePath}/login/{registration} and {basePath}/callback, sign-out at {basePath}/logout
     * (ending the Sangam session too), and answers a missing sign-in with a redirect, or 401 with {@code login} to a fetch.
     * Authorise your own routes as usual, before or after.
     */
    public static HttpSecurity apply(HttpSecurity http, ClientRegistrationRepository registrations, SangamProperties properties) throws Exception {
        String base = properties.getBasePath();
        AuthenticationSuccessHandler comeBack = (request, response, authentication) -> {
            Object target = request.getSession().getAttribute(SangamAuthorizationRequestResolver.RETURN_TO);
            request.getSession().removeAttribute(SangamAuthorizationRequestResolver.RETURN_TO);
            response.sendRedirect(SangamAuthorizationRequestResolver.safeReturn(target instanceof String s ? s : "/"));
        };
        OidcClientInitiatedLogoutSuccessHandler logout = new OidcClientInitiatedLogoutSuccessHandler(registrations);
        logout.setPostLogoutRedirectUri("{baseUrl}/");
        http.oauth2Login(login -> login
                        .authorizationEndpoint(a -> a.baseUri(base + "/login").authorizationRequestResolver(new SangamAuthorizationRequestResolver(registrations, base + "/login")))
                        .redirectionEndpoint(r -> r.baseUri(base + "/callback"))
                        .successHandler(comeBack))
                .logout(l -> l.logoutRequestMatcher(PathPatternRequestMatcher.withDefaults().matcher(HttpMethod.GET, base + "/logout")).logoutSuccessHandler(logout))
                .exceptionHandling(e -> e.authenticationEntryPoint((request, response, ex) -> send(request, response, loginUrl(properties, pathOf(request), null, null), "sign_in_required")));
        return http;
    }

    static String pathOf(HttpServletRequest request) {
        return request.getRequestURI() + (request.getQueryString() == null ? "" : "?" + request.getQueryString());
    }

    static void send(HttpServletRequest request, HttpServletResponse response, String login, String error) throws IOException {
        if (wantsJson(request)) {
            response.setStatus(401);
            response.setContentType("application/json");
            response.getWriter().write("{\"error\":\"" + error + "\",\"login\":\"" + login.replace("\"", "%22") + "\"}");
        } else {
            response.sendRedirect(login);
        }
    }
}
