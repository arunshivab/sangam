package in.sangamid.spring;

import in.sangamid.client.SangamUser;
import in.sangamid.client.StepUp;
import jakarta.servlet.http.HttpServletRequest;
import jakarta.servlet.http.HttpServletResponse;
import org.springframework.web.method.HandlerMethod;
import org.springframework.web.servlet.HandlerInterceptor;

/** Enforces {@link RequireStepUp} on Spring MVC handlers. */
public final class StepUpInterceptor implements HandlerInterceptor {
    private final SangamProperties properties;

    public StepUpInterceptor(SangamProperties properties) {
        this.properties = properties;
    }

    @Override
    public boolean preHandle(HttpServletRequest request, HttpServletResponse response, Object handler) throws Exception {
        if (!(handler instanceof HandlerMethod method)) {
            return true;
        }
        RequireStepUp requirement = method.getMethodAnnotation(RequireStepUp.class);
        if (requirement == null) {
            requirement = method.getBeanType().getAnnotation(RequireStepUp.class);
        }
        if (requirement == null) {
            return true;
        }
        Long maxAge = requirement.maxAge() < 0 ? null : requirement.maxAge();
        SangamUser user = SangamUsers.current();
        if (StepUp.satisfies(user, requirement.acr(), maxAge)) {
            return true;
        }
        String back = requirement.returnTo().isEmpty() ? SangamSecurity.pathOf(request) : requirement.returnTo();
        SangamSecurity.send(request, response, SangamSecurity.loginUrl(properties, back, requirement.acr(), maxAge), "step_up_required");
        return false;
    }
}
