import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import App from './App';
import { FeedbackProvider } from './feedback';
import { applyStoredTheme } from './theme';
import { applyDesign, readDesign } from './design';
import '@fontsource-variable/inter/wght.css';
import './styles.css';
import './design.css';
import './designs/base.css';
import './designs/leitfaden.css';
import './designs/kontrollraum.css';
import './designs/magazin.css';
import './designs/bento.css';
import './designs/fokus.css';

applyStoredTheme();
applyDesign(readDesign());

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <FeedbackProvider>
        <App />
      </FeedbackProvider>
    </BrowserRouter>
  </StrictMode>,
);
