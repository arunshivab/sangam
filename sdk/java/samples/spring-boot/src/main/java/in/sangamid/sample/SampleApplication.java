package in.sangamid.sample;

import in.sangamid.spring.SangamConfiguration;
import in.sangamid.spring.SangamProperties;
import in.sangamid.spring.SangamSecurity;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Import;
import org.springframework.security.config.annotation.web.builders.HttpSecurity;
import org.springframework.security.oauth2.client.registration.ClientRegistrationRepository;
import org.springframework.security.web.SecurityFilterChain;

/** Sample: Spring Boot signing in with Sangam. Sign-in, a permission check, step-up for a signature, a shared audit event. */
@SpringBootApplication
@Import(SangamConfiguration.class)
public class SampleApplication {

    public static void main(String[] args) {
        SpringApplication.run(SampleApplication.class, args);
    }

    @Bean
    SecurityFilterChain security(HttpSecurity http, ClientRegistrationRepository registrations, SangamProperties sangam) throws Exception {
        http.authorizeHttpRequests(a -> a.requestMatchers("/", "/error").permitAll().anyRequest().authenticated());
        return SangamSecurity.apply(http, registrations, sangam).build();
    }
}
