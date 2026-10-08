package in.sangamid.spring;

import jakarta.servlet.http.HttpServletRequest;
import java.util.LinkedHashMap;
import java.util.Map;
import org.springframework.security.oauth2.client.registration.ClientRegistrationRepository;
import org.springframework.security.oauth2.client.web.DefaultOAuth2AuthorizationRequestResolver;
import org.springframework.security.oauth2.client.web.OAuth2AuthorizationRequestCustomizers;
import org.springframework.security.oauth2.client.web.OAuth2AuthorizationRequestResolver;
import org.springframework.security.oauth2.core.endpoint.OAuth2AuthorizationRequest;

/**
 * Adds PKCE (S256) to every authorization request — Sangam requires it, and Spring adds it only for public clients —
 * and passes a step-up requirement ({@code acr}, {@code max_age}) and where to come back to ({@code returnTo}).
 */
public final class SangamAuthorizationRequestResolver implements OAuth2AuthorizationRequestResolver {
    /** The session attribute that remembers where to go after sign-in. */
    public static final String RETURN_TO = "sangam.returnTo";
    private final DefaultOAuth2AuthorizationRequestResolver inner;

    public SangamAuthorizationRequestResolver(ClientRegistrationRepository registrations, String authorizationBaseUri) {
        inner = new DefaultOAuth2AuthorizationRequestResolver(registrations, authorizationBaseUri);
        inner.setAuthorizationRequestCustomizer(OAuth2AuthorizationRequestCustomizers.withPkce());
    }

    @Override
    public OAuth2AuthorizationRequest resolve(HttpServletRequest request) {
        return customise(request, inner.resolve(request));
    }

    @Override
    public OAuth2AuthorizationRequest resolve(HttpServletRequest request, String clientRegistrationId) {
        return customise(request, inner.resolve(request, clientRegistrationId));
    }

    /** Only local paths are followed after sign-in. */
    static String safeReturn(String value) {
        return value != null && value.startsWith("/") && !value.startsWith("//") && !value.startsWith("/\\") ? value : "/";
    }

    private static OAuth2AuthorizationRequest customise(HttpServletRequest request, OAuth2AuthorizationRequest authorization) {
        if (authorization == null) {
            return null;
        }
        request.getSession(true).setAttribute(RETURN_TO, safeReturn(request.getParameter("returnTo")));
        Map<String, Object> extra = new LinkedHashMap<>(authorization.getAdditionalParameters());
        String acr = request.getParameter("acr");
        String maxAge = request.getParameter("max_age");
        if (acr != null && !acr.isEmpty()) {
            extra.put("acr_values", acr);
        }
        if (maxAge != null && maxAge.matches("\\d+")) {
            extra.put("max_age", maxAge);
        }
        return OAuth2AuthorizationRequest.from(authorization).additionalParameters(extra).build();
    }
}
