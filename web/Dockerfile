# Angular dev server for docker compose; production hosting is out of scope
# Keep the major in sync with web/.nvmrc
FROM node:26-slim
WORKDIR /app
# The dev server writes its cache under the workspace (.angular/cache), so the node user owns /app
RUN chown node:node /app
USER node
COPY --chown=node:node package.json package-lock.json ./
RUN npm ci --ignore-scripts
COPY --chown=node:node angular.json tsconfig.json tsconfig.app.json proxy.conf.mjs ./
COPY --chown=node:node public/ public/
COPY --chown=node:node src/ src/
EXPOSE 4200
CMD ["npm", "start"]
