import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Auth0Provider } from '@auth0/auth0-react'
import './index.css'
import App from './App.tsx'

type ClientConfig = { auth0Domain: string; auth0ClientId: string; auth0Audience: string }
const root = createRoot(document.getElementById('root')!)

const start = async () => {
  const localConfig = import.meta.env.VITE_AUTH0_DOMAIN ? {
    auth0Domain: import.meta.env.VITE_AUTH0_DOMAIN,
    auth0ClientId: import.meta.env.VITE_AUTH0_CLIENT_ID,
    auth0Audience: import.meta.env.VITE_AUTH0_AUDIENCE,
  } as ClientConfig : null
  const config = (localConfig ?? await fetch('/config/client.json', { credentials: 'same-origin' })
    .then((response) => response.ok
      ? response.json() as Promise<ClientConfig>
      : Promise.reject(new Error('public client configuration unavailable')))) as ClientConfig
  if (!config.auth0Domain || !config.auth0ClientId || !config.auth0Audience)
    throw new Error('incomplete public client configuration')

  root.render(
    <StrictMode>
      <Auth0Provider
        domain={config.auth0Domain}
        clientId={config.auth0ClientId}
        authorizationParams={{ redirect_uri: window.location.origin, audience: config.auth0Audience }}
      >
        <App />
      </Auth0Provider>
    </StrictMode>,
  )
}

void start().catch(() => root.render(<p>No se pudo cargar la configuración pública de autenticación.</p>))
