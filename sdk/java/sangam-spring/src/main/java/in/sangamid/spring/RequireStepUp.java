package in.sangamid.spring;

import java.lang.annotation.Documented;
import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * The handler needs a sign-in at this level, recent enough (SGM-207). Otherwise the browser goes back to Sangam with
 * {@code acr_values} (and {@code max_age}), or a fetch gets 401 with where to go.
 */
@Documented
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.TYPE})
public @interface RequireStepUp {
    /** The level, for example {@link in.sangamid.client.SangamAcr#SIGNATURE}. */
    String acr();

    /** How recent, in seconds; negative for no limit (a signature level is capped at five minutes anyway). */
    long maxAge() default -1;

    /** Where to come back to after stepping up; the request's own path when empty. */
    String returnTo() default "";
}
