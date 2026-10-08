package in.sangamid.spring;

import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.web.servlet.config.annotation.InterceptorRegistry;
import org.springframework.web.servlet.config.annotation.WebMvcConfigurer;

/** Import this ({@code @Import(SangamConfiguration.class)}) for {@link RequireStepUp} and the audit recorder. */
@Configuration(proxyBeanMethods = false)
@EnableConfigurationProperties(SangamProperties.class)
public class SangamConfiguration implements WebMvcConfigurer {
    private final SangamProperties properties;

    public SangamConfiguration(SangamProperties properties) {
        this.properties = properties;
    }

    @Bean
    public SangamAuditRecorder sangamAuditRecorder() {
        return new SangamAuditRecorder(properties);
    }

    @Override
    public void addInterceptors(InterceptorRegistry registry) {
        registry.addInterceptor(new StepUpInterceptor(properties));
    }
}
