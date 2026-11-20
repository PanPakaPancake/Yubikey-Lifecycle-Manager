import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router';
import App from './App';
import { FeedbackProvider } from './feedback';
import { applyStoredTheme } from './theme';
import '@fontsource-variable/inter/wght.css';
import './styles.css';
import './design.css';
import './bento.css';

applyStoredTheme();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <FeedbackProvider>
        <App />
      </FeedbackProvider>
    </BrowserRouter>
  </StrictMode>,
);
