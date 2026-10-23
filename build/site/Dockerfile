# tdpdf.com. The pages are built BEFORE this, by build-site.ps1 into build/site/out
# (.github/workflows/site.yml does both). This image only serves them.
#
#   pwsh -File build/site/build-site.ps1 -OutputDir build/site/out
#   docker build -t tdpdf-site build/site
FROM nginxinc/nginx-unprivileged:1.29-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY out/ /usr/share/nginx/html/
