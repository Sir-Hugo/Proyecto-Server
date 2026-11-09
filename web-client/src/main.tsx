import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Auth0Provider } from '@auth0/auth0-react'
import './index.css'
import App from './App.tsx'

const root = createRoot(document.getElementById('root')!)
const { VITE_AUTH0_DOMAIN, VITE_AUTH0_CLIENT_ID, VITE_AUTH0_AUDIENCE } =
  import.meta.env

if (!VITE_AUTH0_DOMAIN || !VITE_AUTH0_CLIENT_ID || !VITE_AUTH0_AUDIENCE) {
  root.render(
    <p>
      Falta configuración de Auth0. Revisa las variables VITE_AUTH0_* en
      web-client/.env.local y reinicia Vite.
    </p>,
  )
} else {
  root.render(
    <StrictMode>
      <Auth0Provider
        domain={VITE_AUTH0_DOMAIN}
        clientId={VITE_AUTH0_CLIENT_ID}
        authorizationParams={{
          redirect_uri: window.location.origin,
          audience: VITE_AUTH0_AUDIENCE,
        }}
      >
        <App />
      </Auth0Provider>
    </StrictMode>,
  )
}
